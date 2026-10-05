USE BusTicketSaleSystem;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_Routes_Origin_Destination')
    CREATE INDEX IX_Routes_Origin_Destination ON Routes(Origin, Destination);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_Trips_DepartureTime')
    CREATE INDEX IX_Trips_DepartureTime ON Trips(DepartureTime);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_Tickets_Account_Status')
    CREATE INDEX IX_Tickets_Account_Status ON Tickets(AccountId, Status, BookingTime DESC);
GO
