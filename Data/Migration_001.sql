USE BusTicketSaleSystem;
GO
IF COL_LENGTH('Tickets','TicketCode') IS NULL ALTER TABLE Tickets ADD TicketCode NVARCHAR(30) NULL;
IF COL_LENGTH('Tickets','PassengerName') IS NULL ALTER TABLE Tickets ADD PassengerName NVARCHAR(100) NULL;
IF COL_LENGTH('Tickets','PassengerPhone') IS NULL ALTER TABLE Tickets ADD PassengerPhone NVARCHAR(20) NULL;
IF COL_LENGTH('Tickets','Price') IS NULL ALTER TABLE Tickets ADD Price DECIMAL(10,0) NULL;
IF COL_LENGTH('Tickets','PaymentStatus') IS NULL ALTER TABLE Tickets ADD PaymentStatus NVARCHAR(20) NULL;
IF COL_LENGTH('Tickets','CancelledAt') IS NULL ALTER TABLE Tickets ADD CancelledAt DATETIME2 NULL;
GO
UPDATE Tickets SET TicketCode=N'LEGACY-'+CONVERT(NVARCHAR(20),TicketId) WHERE TicketCode IS NULL;
UPDATE Tickets SET PassengerName=(SELECT FullName FROM Accounts a WHERE a.AccountId=Tickets.AccountId) WHERE PassengerName IS NULL;
UPDATE Tickets SET Price=(SELECT Price FROM Trips t WHERE t.TripId=Tickets.TripId) WHERE Price IS NULL;
UPDATE Tickets SET PaymentStatus=N'Chưa thanh toán' WHERE PaymentStatus IS NULL;
ALTER TABLE Tickets ALTER COLUMN TicketCode NVARCHAR(30) NOT NULL;
ALTER TABLE Tickets ALTER COLUMN PassengerName NVARCHAR(100) NOT NULL;
ALTER TABLE Tickets ALTER COLUMN Price DECIMAL(10,0) NOT NULL;
ALTER TABLE Tickets ALTER COLUMN PaymentStatus NVARCHAR(20) NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Tickets_TicketCode') CREATE UNIQUE INDEX UX_Tickets_TicketCode ON Tickets(TicketCode);
GO
