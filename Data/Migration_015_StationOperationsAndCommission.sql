/* Migration 015 – Station Operations, Trip Status, and Bus Company Commission.

   1. Adds Trips.Status to support trip cancellation and operational lifecycle:
      - 'Scheduled': normal upcoming or active trip.
      - 'Departed': bus has left the station.
      - 'Cancelled': operator cancelled the trip (triggers automated ticket cancellation).
   2. Adds BusCompanies.CommissionRate (default 10% = 0.1000) to allow calculating
      bus station commission vs payout owed to each bus operator.
   3. Backfills existing rows idempotently.

   Reversal:
      ALTER TABLE dbo.Trips DROP CONSTRAINT DF_Trips_Status;
      ALTER TABLE dbo.Trips DROP CONSTRAINT CK_Trips_Status;
      ALTER TABLE dbo.Trips DROP COLUMN Status;
      ALTER TABLE dbo.BusCompanies DROP CONSTRAINT DF_BusCompanies_CommissionRate;
      ALTER TABLE dbo.BusCompanies DROP COLUMN CommissionRate;
*/

/* 1. Add Trips.Status */
IF COL_LENGTH(N'dbo.Trips', N'Status') IS NULL
BEGIN
    ALTER TABLE dbo.Trips ADD Status NVARCHAR(20) NOT NULL
        CONSTRAINT DF_Trips_Status DEFAULT N'Scheduled';
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Trips_Status')
BEGIN
    ALTER TABLE dbo.Trips ADD CONSTRAINT CK_Trips_Status
        CHECK (Status IN (N'Scheduled', N'Departed', N'Cancelled'));
END;
GO

/* Update status of past trips that were already completed */
UPDATE dbo.Trips
SET Status = N'Departed'
WHERE Status = N'Scheduled' AND DepartureTime < SYSUTCDATETIME();
GO

/* 2. Add BusCompanies.CommissionRate */
IF COL_LENGTH(N'dbo.BusCompanies', N'CommissionRate') IS NULL
BEGIN
    ALTER TABLE dbo.BusCompanies ADD CommissionRate DECIMAL(5,4) NOT NULL
        CONSTRAINT DF_BusCompanies_CommissionRate DEFAULT 0.1000;
END;
GO
