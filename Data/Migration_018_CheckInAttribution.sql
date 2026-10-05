/* Migration 018 – Record who checked in the ticket (Requirement 4).
   Adds CheckedInByAccountId column referencing Accounts(AccountId).
*/

IF COL_LENGTH(N'dbo.Tickets', N'CheckedInByAccountId') IS NULL
BEGIN
    ALTER TABLE dbo.Tickets
    ADD CheckedInByAccountId INT NULL
    CONSTRAINT FK_Tickets_CheckedInBy FOREIGN KEY REFERENCES dbo.Accounts(AccountId);
END;
GO
