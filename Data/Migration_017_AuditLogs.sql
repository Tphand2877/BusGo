/* Migration 017 – Owner role and role-change audit log.
   1. Update CK_Accounts_Role check constraint to allow 'Owner'.
   2. Create table dbo.AuditLogs for tracking role changes.
*/

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Accounts_Role')
BEGIN
    ALTER TABLE dbo.Accounts DROP CONSTRAINT CK_Accounts_Role;
END;
GO

ALTER TABLE dbo.Accounts ADD CONSTRAINT CK_Accounts_Role CHECK (Role IN (N'Customer', N'Admin', N'Owner'));
GO

IF OBJECT_ID(N'dbo.AuditLogs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLogs
    (
        Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLogs PRIMARY KEY,
        ActorId   INT NULL,
        TargetId  INT NOT NULL,
        Action    NVARCHAR(50) NOT NULL,
        OldValue  NVARCHAR(50) NULL,
        NewValue  NVARCHAR(50) NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_AuditLogs_CreatedAt DEFAULT SYSUTCDATETIME()
    );

    CREATE INDEX IX_AuditLogs_CreatedAt ON dbo.AuditLogs (CreatedAt DESC);
END;
GO
