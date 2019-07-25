using Dapper;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.Network.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.Storage.Fluent;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace StretchBuildstrong.Function
{
    public enum AgentStatus
    {
        Provisioning = 1,
        Ready = 2,
        Building = 3,
        Deprovisioning = 4,
        Done = 5
    }

    public static class ManageBuildAgents
    {
        private static ILogger _log;
        private static IAzure _azure;
        private static SqlConnection _db;
        private static IConfigurationRoot _cfg;

        [FunctionName("ManageBuildAgents")]
        public static void Run([TimerTrigger("0 * * * * *")]TimerInfo myTimer, ILogger log, ExecutionContext context)
        {
            _log = log;

            _cfg = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var creds = SdkContext.AzureCredentialsFactory.FromServicePrincipal(_cfg["CLIENT_ID"], _cfg["CLIENT_SECRET"], _cfg["TENANT_ID"], AzureEnvironment.AzureGlobalCloud);
            _azure = Azure.Authenticate(creds).WithSubscription(_cfg["SUBSCRIPTION_ID"]);

            // TODO: Check if another function is already running, and exit so we don't overlap
            // Azure Functions may already handle this - not sure

            // TODO: Need to detect if VM creation/deletion fails and cleanup appropriately

            using (_db = new SqlConnection(_cfg.GetConnectionString("SqlConnectionString")))
            {
                UpdateBuildAgents().Wait();
                ReplenishAgentPool().Wait();
            }
        }

        private static async Task ReplenishAgentPool()
        {
            var provisioningCount = GetProvisioningCount();
            var readyCount = GetReadyCount();
            var queuedCount = await GetQueuedCount();

            var replenishCount = int.Parse(_cfg["POOL_MIN_SIZE"]) - readyCount - provisioningCount + queuedCount;

            _log.LogInformation($"Creating {replenishCount} new agents ({_cfg["POOL_MIN_SIZE"]} pool min - {readyCount} ready - {provisioningCount} creating + {queuedCount} queued)...");

            CreateBuildServers(replenishCount);
        }

        private static async Task<int> GetQueuedCount()
        {
            var url = $"{_cfg["DEVOPS_URL"]}/_apis/distributedtask/pools/{_cfg["POOL_ID"]}/jobrequests?api-version=5.1-preview.1";

            _log.LogInformation($"Getting list of queued builds from Azure Pipelines...");

            var content = await DevOpsApi(async client =>
            {
                using (var response = await client.GetAsync(url))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync();
                }
            });

            var data = JObject.Parse(content);

            return data["value"].Children()
                        .Where(x => x["reservedAgent"] == null)
                        .Count();
        }

        private static async Task<string> DevOpsApi(Func<HttpClient, Task<string>> action)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var bearerToken = Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Format("{0}:{1}", "", _cfg["DEVOPS_PAT"])));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", bearerToken);

                return await action(client);
            }
        }

        private static async Task DevOpsApi(Func<HttpClient, Task> action)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var bearerToken = Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Format("{0}:{1}", "", _cfg["DEVOPS_PAT"])));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", bearerToken);

                await action(client);
            }
        }

        private static int GetReadyCount()
        {
            var sql = "SELECT COUNT(*) FROM BuildAgents WHERE Status = @Status";
            return _db.ExecuteScalar<int>(sql, new { Status = AgentStatus.Ready.ToString("d") });
        }

        private static int GetProvisioningCount()
        {
            var sql = "SELECT COUNT(*) FROM BuildAgents WHERE Status = @Status";
            return _db.ExecuteScalar<int>(sql, new { Status = AgentStatus.Provisioning.ToString("d") });
        }

        private static async Task UpdateBuildAgents()
        {
            var agentData = await GetAgentData();

            var readyAgents = ParseReadyAgents(agentData);
            var buildingAgents = ParseBuildingAgents(agentData);
            var offlineAgents = ParseOfflineAgents(agentData);

            _log.LogInformation($"{readyAgents.Count()} agents ready [{string.Join(", ", readyAgents)}]");
            _log.LogInformation($"{buildingAgents.Count()} agents building [{string.Join(", ", buildingAgents.Select(x => x.name))}]");
            _log.LogInformation($"{offlineAgents.Count()} agents offline [{string.Join(", ", offlineAgents.Select(x => x.name))}]");

            foreach (var onlineAgent in readyAgents)
            {
                SetAgentReady(onlineAgent);
            }

            foreach (var (name, id, enabled) in buildingAgents)
            {
                await SetAgentBuilding(name, id, enabled);
            }

            foreach (var (name, id) in offlineAgents)
            {
                if (IsAgentInDatabase(name))
                {
                    if (IsAgentInOnlineStatus(name))
                    {
                        SetAgentDeprovisioning(name, id);
                        _log.LogInformation($"Deleting VM: {name}");
                        DeleteAgentVM(name);
                    }
                }
                else
                {
                    _log.LogInformation($"Offline agent found without a DB record [{name}] - skipping");
                }
            }

            // If there are no agents in pool, any queued builds will fail
            // So only delete the offline ones, if there's at least one online agent
            if (readyAgents.Count() + buildingAgents.Count() > 0)
            {
                await FinalizeDeProvisioning();
            }
        }

        private static bool IsAgentInOnlineStatus(string agentName)
        {
            var sql = $"SELECT COUNT(*) FROM BuildAgents WHERE VMName = @VMName AND Status IN @Status";
            var count = _db.ExecuteScalar<int>(sql, new { VMName = agentName, Status = new[] { AgentStatus.Provisioning.ToString("d"), AgentStatus.Ready.ToString("d"), AgentStatus.Building.ToString("d") } });

            return count > 0;
        }

        private static async Task FinalizeDeProvisioning()
        {
            var sql = $"SELECT VMName, VMResourceId, NICResourceId, IPResourceId, DiskResourceId, AgentId FROM BuildAgents WHERE Status = @Status";
            var agents = _db.Query<(string VMName,
                                    string VMResourceId,
                                    string NICResourceId,
                                    string IPResourceId,
                                    string DiskResourceId,
                                    int AgentId)>(sql, new { Status = AgentStatus.Deprovisioning.ToString("d") });

            foreach (var agent in agents)
            {
                var vm = _azure.VirtualMachines.GetById(agent.VMResourceId);

                if (vm == null)
                {
                    sql = $"UPDATE BuildAgents SET Status = {AgentStatus.Done.ToString("d")}, DateDeprovisioned = @DateDeprovisioned WHERE VMName = @VMName";
                    _db.Execute(sql, new { DateDeprovisioned = DateTime.Now, VMName = agent.VMName });

                    _log.LogInformation($"Deleting NIC: {agent.NICResourceId}...");
                    _azure.NetworkInterfaces.DeleteById(agent.NICResourceId);

                    _log.LogInformation($"Deleting IP: {agent.IPResourceId}...");
                    _azure.PublicIPAddresses.DeleteByIdAsync(agent.IPResourceId);

                    _log.LogInformation($"Deleting Disk: {agent.DiskResourceId}...");
                    _azure.Disks.DeleteByIdAsync(agent.DiskResourceId);

                    await RemovePipelinesAgent(agent.AgentId);
                }
            }
        }

        private static async Task RemovePipelinesAgent(int agentId)
        {
            var url = $"{_cfg["DEVOPS_URL"]}/_apis/distributedtask/pools/{_cfg["POOL_ID"]}/agents/{agentId}?api-version=5.1-preview.1";

            _log.LogInformation($"Deleting agent from Azure Pipelines [{agentId}]...");

            await DevOpsApi(async client =>
            {
                using (var response = await client.DeleteAsync(url))
                {
                    response.EnsureSuccessStatusCode();
                }
            });
        }

        private static void SetAgentDeprovisioning(string agentName, int agentId)
        {
            var vmId = $"/subscriptions/{_cfg["SUBSCRIPTION_ID"]}/resourceGroups/{_cfg["RESOURCE_GROUP"]}/providers/Microsoft.Compute/virtualMachines/{agentName}";
            var vm = _azure.VirtualMachines.GetById(vmId);

            var nic = vm.PrimaryNetworkInterfaceId;
            var ip = vm.GetPrimaryPublicIPAddress().Id;
            var disk = vm.OSDiskId;

            var sql = "UPDATE BuildAgents SET Status = @Status, DateDeprovisioning = @DateDeprovisioning, AgentId = @AgentId, NICResourceId = @NICResourceId, IPResourceId = @IPResourceId, DiskResourceId = @DiskResourceId WHERE VMName = @VMName";
            _db.Execute(sql, new
            {
                VMName = agentName,
                DateDeprovisioning = DateTime.Now,
                AgentId = agentId,
                NICResourceId = nic,
                IPResourceId = ip,
                DiskResourceId = disk,
                Status = AgentStatus.Deprovisioning.ToString("d")
            });
        }

        private static bool IsAgentInDatabase(string agentName)
        {
            var sql = "SELECT COUNT(*) FROM BuildAgents WHERE VMName = @VMName";
            var count = _db.ExecuteScalar<int>(sql, new { VMName = agentName });

            return count > 0;
        }

        private static async Task SetAgentBuilding(string agentName, int agentId, bool enabled)
        {
            var sql = "UPDATE BuildAgents SET Status = @Status, DateBuilding = @DateBuilding, AgentId = @AgentId WHERE VMName = @VMName";
            _db.Execute(sql, new
            {
                VMName = agentName,
                DateBuilding = DateTime.Now,
                AgentId = agentId,
                Status = AgentStatus.Building.ToString("d")
            });

            if (enabled)
            {
                await DisableAgent(agentId);
            }
        }

        private static async Task DisableAgent(int agentId)
        {
            var url = $"{_cfg["DEVOPS_URL"]}/_apis/distributedtask/pools/{_cfg["POOL_ID"]}/agents/{agentId}?api-version=5.0";
            var body = new StringContent($"{{\"enabled\":false,\"id\":{agentId}}}", Encoding.UTF8, "application/json");

            _log.LogInformation($"Disabling agent in Azure Pipelines [{agentId}]...");

            await DevOpsApi(async client =>
            {
                using (var response = await client.PatchAsync(url, body))
                {
                    response.EnsureSuccessStatusCode();
                }
            });
        }

        private static void SetAgentReady(string agentName)
        {
            var sql = "UPDATE BuildAgents SET Status = @Status, DateReady = @DateReady WHERE VMName = @VMName";
            _db.Execute(sql, new { VMName = agentName, DateReady = DateTime.Now, Status = AgentStatus.Ready.ToString("d") });
        }

        private async static Task<string> GetAgentData()
        {
            var url = $"{_cfg["DEVOPS_URL"]}/_apis/distributedtask/pools/{_cfg["POOL_ID"]}/agents?includeAssignedRequest=true&includeLastCompletedRequest=true&api-version=5.1-preview.1";

            _log.LogInformation($"Retrieving agent data from Azure Pipelines...");

            return await DevOpsApi(async client =>
            {
                using (var response = await client.GetAsync(url))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync();
                }
            });
        }

        private static IEnumerable<(string name, int id)> ParseOfflineAgents(string responseBody)
        {
            var data = JObject.Parse(responseBody);

            // An agent will show as offline for a few seconds right after it is first provisioned before flipping to online
            // By filtering by lastCompletedRequest we won't count those in the offline list (which triggers deprovisioning)
            return data["value"].Children()
                                .Where(x => (string)x["status"] == "offline" && x["lastCompletedRequest"] != null)
                                .Select(x => ((string)x["name"], (int)x["id"]));
        }

        private static IEnumerable<(string name, int id, bool enabled)> ParseBuildingAgents(string responseBody)
        {
            var data = JObject.Parse(responseBody);

            return data["value"].Children()
                                .Where(x => x["assignedRequest"] != null && x["lastCompletedRequest"] == null)
                                .Select(x => ((string)x["name"], (int)x["id"], (bool)x["enabled"]));

        }

        private static IEnumerable<string> ParseReadyAgents(string responseBody)
        {
            var data = JObject.Parse(responseBody);

            return data["value"].Children()
                                .Where(x => (string)x["status"] == "online" && x["assignedRequest"] == null && x["lastCompletedRequest"] == null)
                                .Select(x => (string)x["name"]);
        }

        private static void DeleteAgentVM(string vmName)
        {
            var vmId = $"/subscriptions/{_cfg["SUBSCRIPTION_ID"]}/resourceGroups/{_cfg["RESOURCE_GROUP"]}/providers/Microsoft.Compute/virtualMachines/{vmName}";

            _log.LogInformation($"Deleting VM: {vmId}...");
            _azure.VirtualMachines.DeleteByIdAsync(vmId);
        }

        private static void CreateBuildServers(int count)
        {
            var vnet = _azure.Networks.GetById(_cfg["VNET_ID"]);
            var storage = _azure.StorageAccounts.GetById(_cfg["STORAGE_ACCOUNT_ID"]);

            for (var x = 0; x < count; x++)
            {
                var insertSql = "INSERT INTO BuildAgents(DateCreated) VALUES(@DateCreated); SELECT SCOPE_IDENTITY();";
                var id = _db.ExecuteScalar<int>(insertSql, new { DateCreated = DateTime.Now });

                var vmName = $"{_cfg["VM_PREFIX"]}{id.ToString()}";

                var vmId = $"/subscriptions/{_cfg["SUBSCRIPTION_ID"]}/resourceGroups/{_cfg["RESOURCE_GROUP"]}/providers/Microsoft.Compute/virtualMachines/{vmName}";
                var sql = "UPDATE BuildAgents SET VMName = @VMName, VMResourceId = @VMResourceId, Status = @Status WHERE Id = @Id";

                _db.Execute(sql, new
                {
                    VMName = vmName,
                    VMResourceId = vmId,
                    Id = id,
                    Status = AgentStatus.Provisioning.ToString("d")
                });

                CreateBuildServer(vmName, vnet, storage);
            }
        }

        private static void CreateBuildServer(string vmName, INetwork vnet, IStorageAccount storage)
        {
            _log.LogInformation("Creating IP/NIC...");

            var ip = _azure.PublicIPAddresses.Define(vmName)
                .WithRegion(_cfg["REGION"])
                .WithExistingResourceGroup(_cfg["RESOURCE_GROUP"])
                .WithLeafDomainLabel(vmName);

            var nic = _azure.NetworkInterfaces.Define(vmName)
                .WithRegion(_cfg["REGION"])
                .WithExistingResourceGroup(_cfg["RESOURCE_GROUP"])
                .WithExistingPrimaryNetwork(vnet)
                .WithSubnet(_cfg["SUBNET_NAME"])
                .WithPrimaryPrivateIPAddressDynamic()
                .WithNewPrimaryPublicIPAddress(ip)
                .Create();

            _log.LogInformation($"Creating VM: {vmName}...");

            _azure.VirtualMachines.Define(vmName)
                .WithRegion(_cfg["REGION"])
                .WithExistingResourceGroup(_cfg["RESOURCE_GROUP"])
                .WithExistingPrimaryNetworkInterface(nic)
                .WithLinuxCustomImage(_cfg["CUSTOM_IMAGE"])
                .WithRootUsername(_cfg["ADMIN_USERNAME"])
                .WithRootPassword(_cfg["ADMIN_PASSWORD"])
                .WithSize(_cfg["VM_SIZE"])
                .WithSystemAssignedManagedServiceIdentity()
                .WithBootDiagnostics(storage)
                .DefineNewExtension("CustomScript")
                    .WithPublisher("Microsoft.Azure.Extensions")
                    .WithType("CustomScript")
                    .WithVersion("2.0")
                    .WithMinorVersionAutoUpgrade()
                    .WithPublicSetting("fileUris", new List<string>() { _cfg["AGENT_INSTALL_SCRIPT"] })
                    .WithPublicSetting("commandToExecute", _cfg["AGENT_INSTALL_COMMAND"] + $" {_cfg["DEVOPS_URL"]} {_cfg["DEVOPS_PAT"]} {_cfg["POOL_NAME"]} {vmName}")
                    .Attach()
                .CreateAsync();
        }
    }
}