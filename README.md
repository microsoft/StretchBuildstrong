# Stretch Buildstrong - Elastic private build servers for Azure Pipelines

## What's the problem we're trying to solve?
Azure Pipelines provides a [hosted build service](https://docs.microsoft.com/en-us/azure/devops/pipelines/agents/hosted?view=azure-devops) which is pretty awesome.  You can create a new pipeline and have it up and running within minutes on Microsoft provided build machines (Windows, Linux and Mac options available). The best part about that is you don't have to worry about managing your own VM's and build infrastructure, you get elastic scale where new VM's are just magically created as needed, and you get a fresh VM for every build, giving you repeatable and predictable build environments.  Sounds pretty great right?

However, there are a few common reasons why teams may not be able to use the hosted build service:

1. Your builds need to connect to on-premise servers/services which sit behind a firewall (e.g. SonarQube, Artifactory, etc).  Hosted build servers can only connect to things which are internet accessible.

2. You need some software/SDK installed on your build machine which isn't in the Microsoft hosted build images.  The hosted build images contain a pretty extensive list of [software which comes pre-installed](https://github.com/Microsoft/azure-pipelines-image-generation/blob/master/images/win/Vs2019-Server2019-Readme.md), but if you need some SDK which isn't on there...well tough luck (sure you *could* install it as part of the build, but that's often pretty slow).

3. You want a bigger beefier build machine to speed up your builds.  Hosted build servers are always a Standard DS2_v2 (2 cores, 7 GB RAM).

When teams run into these limitations they have to resort to standing up their own private build servers (which could still be done using Azure VM's), but this means they now have to manage and operate those VM's (e.g. security patches); they have to carefully plan capacity and choose the right number of build servers to create - too many and you're wasting money/resources, too few and you're builds will spend a bunch of time sitting in queues; and you don't get the repeatability/predictability benefits of a fresh VM for every build.

## My Solution - Stretch Buildstrong
This project is a way to get (most) of the benefits of hosted build - specifically elasticity, and a fresh VM per build - but with private build servers, that live on a VNet of your choice, using a VM image of your choice, and on a VM size of your choice.

It does this by dynamically provisioning and destroying VM's on demand.  It's not feasible to do this "just-in-time" when a build is queued, because if every build had to wait for a VM to be created, that would likely be unacceptably slow.  Instead it will create a pool of ready-to-go VM's that any queued builds will consume (you configure how big you want this pool to be). It will monitor your builds, and the VM's and automatically destroy the VM's once they are done running a build, and replenish the pool of VM's as needed.

This is implemented as an Azure Function + SQL Azure Database. The source code is published on GitHub [LINK HERE]. There's a bit of a setup/configuration involved to get it up and running.  Instructions for all this are below.

NOTE: I've only tested this with Azure DevOps (in the cloud), not Azure DevOps Server - but in theory it should be able to work with both.  The DevOps API calls may need a couple tweaks, if anybody wants to try it and report back (and/or submit a PR) that would be great.

## Setting it Up
You're going to need to do a bit of setup before you can deploy and kickoff the Azure Function that does the work.

1. Create 2 Resource Groups in Azure.  1 of these will house all the VM's that get created (I called this StretchBuildServers), the other will hold the non VM resources like the Azure Function App and SQL DB (I called this StretchBuild).

2. You'll need a custom VM Image to use. Currently this project only works with Linux images - but the changes needed to support Windows are pretty minimal - I just need to find some time to test it out.  So you'll need a custom Linux VM Image stored in your subscription (be sure it's in the same region as you want to create the VM's in).  NOTE: The image should NOT include the Azure Pipelines agent, but will need any pre-requisites (e.g. .Net Core).  We publish the [scripts we use to create the hosted build images](https://github.com/microsoft/azure-pipelines-image-generation).  I used the Ubuntu 16.04 image for my testing, and created it in my StretchBuild resource group.

3. A Virtual Network to use. This can live in any resource group you want (maybe even a different Azure subscription - not sure about that part, would have to test that).  If you are setting this up for real - you likely already have a VNet somewhere that is configured to be able to talk to stuff behind the firewall - that's what you'll want to use.  If you are just trying to test this out, any VNet will do, just go ahead and create one with the default options (I created mine in the StretchBuildServers resource group).  Note: You will likely want a dedicated subnet on the VNet to use for these elastic servers - when I created my VNet I modified the IP range for the subnet to have more IP's available to it (all of the IP's in fact) - I set the subnet IP range to 10.2.0.0/16 (the same as the vnet address space).

4. A storage account for the VM diagnostic logs (general purpose v2 - locally redundant storage).  I created one in the StretchBuildServers resource group.

5. An Azure SQL database.  I created this in the StretchBuild resource group. You don't need a really beefy/expensive DB size - we'll be putting very little load on it (about 5-10 queries / min), but I've found if you pick the very cheapest SQL SKU it is REALLY slow.  I picked Standard S1 (20 DTU's - ~$30/month).  Be sure to check the box to Allow Azure services to access server when creating the server.

6. An Azure Function App.  I created this in the StretchBuild resource group, and used the settings Windows OS, Consumption Plan, .Net Core, and let it create a new storage account and a new app insights instance.  Based on my testing the usage should be well under what Azure provides for free on an Azure Function app.

7. [Create an AAD Service Principal](https://docs.microsoft.com/en-us/azure/active-directory/develop/howto-create-service-principal-portal) to use (Note: the Redirect URI doesn't matter).  After creating it go to the Certifactes and secrets blade and create a client secret.  Make note of the Client ID, Tenant ID, and Client Secret - we'll need these soon.  Grant it Contributor access to the 2 resource groups created earlier. If any of your resources (e.g. VM Image, VNet) live in other resource groups/subscriptions, you'll need to give the Service Principal some permissions on those too.

8. Create an Agent Pool in Azure DevOps.  It's best if you have a dedicated pool for Stretch Buildstrong to use. Try not to use spaces or special characters in the name.

9. Create a Personal Access Token in Azure DevOps.  Unfortunately a PAT is the only feasible option I could see to use here. Which means that it will be associated to a specific user, and will have a max expiration of 1 year (so you'll have to refresh and update the Azure Function config at least once a year).  When choosing PAT permissions you'll need to click the Show All Scopes button at the bottom and give it the following: Agent Pools:ReadAndManage, Build:Read, Release:Read.

10. Open up the Azure Function app, and go to the configuration section.  You'll need to create and set the following Application Settings:

    SUBSCRIPTION_ID - The Azure Subscription ID where the VM's will be created.

    CLIENT_ID - From the Service Principal created in step #7 above.

    CLIENT_SECRET - From the Service Principal created in step #7 above.

    TENANT_ID - The AAD Tenant ID of the Service Principal created in step #7 above.

    RESOURCE_GROUP - The Resource Group name where the VM's should be created (StretchBuildServers in my case).

    REGION - The Region where the VM's will be created.  Must be the same region where the VM Image is in.  (I used eastus2). [List of acceptable values here](https://github.com/Azure/azure-libraries-for-net/blob/master/src/ResourceManagement/ResourceManager/Region.cs)

    VNET_ID - The resource id of the Virtual Network. In Azure Portal if you go to the Properties blade of the VNet you can find this value.  It should look something like this: /subscriptions/{SUBSCRIPTION_ID}/resourceGroups/StretchBuildServers/providers/Microsoft.Network/virtualNetworks/StretchBuildNetwork

    SUBNET_NAME - The name of the subnet which the VM's should be created on.  If you created a VNet with the default options this will probably be called "default".

    STORAGE_ACCOUNT_ID - The resource id of the Storage Account that will hold the VM diagnostic logs.  This is found in the Properties blade of the Storage Account, and should look similar to the VNet example above.

    CUSTOM_IMAGE - The resource id of the VM Image.

    ADMIN_USERNAME - The admin/root username to configure when creating the VM's.

    ADMIN_PASSWORD - The admin/root password to configure when creating the VM's.  Be sure this meets the password complexity requirements that Azure imposes.

    VM_SIZE - The VM size to use (i.e. CPU/RAM).  I used "Standard_D2s_v3".  [You can find the list of valid values here](https://github.com/Azure/azure-libraries-for-net/blob/master/src/ResourceManagement/Compute/Generated/Models/VirtualMachineSizeTypes.cs).

    VM_PREFIX - Each VM will be named using the prefix you specify here, with an auto-incrementing number after it. This prefix+number needs to be globally unique, so be creative (StretchBuild is taken!).

    DEVOPS_URL - The URL to your Azure DevOps org (e.g. https://dev.azure.com/DevOpsDylan)

    DEVOPS_PAT - The Personal Access Token you created in step #9.

    POOL_NAME - The name of the Agent Pool you created in step #8 (this is used in the API URL's, so if there's a space in your pool name, you need to do the URL escaping thing with %20's - probably best to just avoid spaces or special chars).

    POOL_ID - The numeric id of your agent pool. To find this go to Organization Settings in Azure DevOps (be sure it's org settings and not project settings), go into Agent Pools, then your newly created Agent Pool.  Then look at the browser url, you should see a poolId=X in there.

    AGENT_INSTALL_SCRIPT - The anonymously accessible URL of a bash script that will install the Azure Pipelines agent on the VM and configure it appropriately. Use this value for testing the service out: https://raw.githubusercontent.com/microsoft/StretchBuildstrong/master/Scripts/install-azure-pipelines-linux-agent.sh.  But if you are deploying this service for real use you should probably fork my repo and reference yours.  I don't want to be responsible for breaking your builds if I make changes to that script.

    AGENT_INSTALL_COMMAND - Assuming you use my script (or a fork of it) from above, this should be set to: sh install-azure-pipelines-linux-agent.sh

    POOL_MIN_SIZE - The target number of idle VM's that you want. The higher the number, the less likely you are to have builds queued up waiting for VM's, but the higher the cost of hosting those VM's.  You can easily change this setting at any time.

11. Create a ConnectionString in the Azure Functions configuration blade.  The name must be SqlConnectionString.  The value you can get from your SQL Database you created earlier, there is a Connection Strings section when you browse to it in the Azure Portal (be sure to replace the placeholders for username and password with the ones you used when you created the SQL Database earlier).  The type should be SQLAzure.

12. Clone the GitHub repo at: https://github.com/microsoft/stretchbuildstrong

13. Open the sln file in Visual Studio, right-click on the StretchBuildstrong.DB project and Publish to the SQL DB created earlier - you'll need to use SQL Authentication with the user/password you set when you created the DB earlier.  NOTE: You will need to first go into your SQL Server resource in Azure Portal, go to the Firewalls and Virtual Networks blade, then click the Add Client IP button (otherwise Visual Studio won't be able to connect).

14. Right-click on StretchBuildstrong.Function project and Publish to the Azure Functions app created earlier (Choose the Run from package file option).


The Azure Function should run every minute, and you can monitor what's going on either in the Azure Functions Monitor blade in the Azure Portal or in the Application Insights instance (go to Log Analytics and look at the traces table).