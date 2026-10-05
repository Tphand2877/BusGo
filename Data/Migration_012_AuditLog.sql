/* Migration 012 – Audit trail for privileged operations (AUD-W1-15 / AUD-D-009).

   Sensitive administrative actions left no durable record: promoting an account to
   Admin wrote nothing at all, and what was written went only to a local rolling text
   file that the application, any local user, and (before AUD-W1-16) an
   unauthenticated loopback caller could rewrite.

   AuditLog is append-only by convention and written inside the same transaction as
   the operation it describes, so a rolled-back operation cannot leave a phantom
   audit row and a committed one cannot be missing its row.

   Reversal: DROP TABLE dbo.AuditLog;  (no other object references it)
*/

IF OBJECT_ID(N'dbo.AuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLog
    (
        AuditId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
        OccurredAt     DATETIME2 NOT NULL CONSTRAINT DF_AuditLog_OccurredAt DEFAULT SYSUTCDATETIME(),
        ActorAccountId INT NULL,          -- NULL only for system/background actions
        Action         NVARCHAR(60)  NOT NULL,
        TargetType     NVARCHAR(40)  NOT NULL,
        TargetId       NVARCHAR(60)  NULL,
        Detail         NVARCHAR(400) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_AuditLog_OccurredAt' AND object_id = OBJECT_ID(N'dbo.AuditLog'))
    CREATE INDEX IX_AuditLog_OccurredAt ON dbo.AuditLog (OccurredAt DESC) INCLUDE (Action, ActorAccountId);
GO
