USE BusTicketSaleSystem;
GO
IF COL_LENGTH('Accounts','Role') IS NULL
    ALTER TABLE Accounts ADD Role NVARCHAR(20) NOT NULL CONSTRAINT DF_Accounts_Role DEFAULT N'Customer';
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_Accounts_Role')
    ALTER TABLE Accounts ADD CONSTRAINT CK_Accounts_Role CHECK (Role IN (N'Customer',N'Admin'));
GO
/*
  1. Register an account in the application.
  2. Run this statement once for the chosen account:
       UPDATE Accounts SET Role=N'Admin' WHERE Username=N'admin';
  Passwords must be created by the application so they are PBKDF2 hashes.
*/
