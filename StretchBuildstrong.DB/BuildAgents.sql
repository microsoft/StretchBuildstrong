CREATE TABLE [dbo].[BuildAgents]
(
	[Id] INT NOT NULL PRIMARY KEY IDENTITY(1, 1), 
    [VMName] VARCHAR(50) NULL, 
    [VMResourceId] VARCHAR(MAX) NULL, 
    [Status] INT NULL, 
    [NICResourceId] VARCHAR(MAX) NULL, 
    [IPResourceId] VARCHAR(MAX) NULL, 
    [DiskResourceId] VARCHAR(MAX) NULL, 
    [DateCreated] DATETIME NULL, 
    [DateReady] DATETIME NULL, 
    [DateBuilding] DATETIME NULL, 
    [DateDeprovisioning] DATETIME NULL, 
    [DateDeprovisioned] DATETIME NULL, 
    [AgentId] INT NULL
)

GO

CREATE INDEX [IX_BuildAgents_VMName] ON [dbo].[BuildAgents] ([VMName])

GO

CREATE INDEX [IX_BuildAgents_Status] ON [dbo].[BuildAgents] ([Status])
