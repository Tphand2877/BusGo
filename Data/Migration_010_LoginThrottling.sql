/* Migration 010 – Login throttling state (AUD-W1-06 / AUD-D-003).

   AuthenticateAsync had no attempt counter, no backoff and no lockout, and logged
   nothing on failure, so online password guessing against any account — including
   Admin — was unlimited and left no trace.

   Two columns on Accounts hold the state. They are nullable/defaulted so existing
   rows remain valid without a data migration, and no existing column is altered.

   Reversal: ALTER TABLE dbo.Accounts DROP CONSTRAINT DF_Accounts_FailedLoginCount;
             ALTER TABLE dbo.Accounts DROP COLUMN FailedLoginCount, LockoutEndsAt;
   Both are metadata-only; no ticket, payment or account data is affected.
*/

IF COL_LENGTH(N'dbo.Accounts', N'FailedLoginCount') IS NULL
    ALTER TABLE dbo.Accounts ADD FailedLoginCount INT NOT NULL
        CONSTRAINT DF_Accounts_FailedLoginCount DEFAULT 0;
GO

IF COL_LENGTH(N'dbo.Accounts', N'LockoutEndsAt') IS NULL
    ALTER TABLE dbo.Accounts ADD LockoutEndsAt DATETIME2 NULL;
GO
