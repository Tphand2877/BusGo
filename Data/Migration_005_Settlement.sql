/* Settlement hardening.
   Adds the used-ticket timestamp and the payment audit trail, and creates the
   anti-double-booking objects that were never applied to databases which
   already had an Accounts table when the migrator first ran (migration
   001_InitialSchema is recorded as applied without executing there).
   Every statement is guarded so the script converges on both a fresh database
   and a legacy hand-built one. */
USE BusTicketSaleSystem;
GO

/* Filtered indexes and the trigger require these session settings; they persist
   for the rest of the connection. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* 1. Ticket used-state timestamp. */
IF COL_LENGTH(N'dbo.Tickets', N'UsedAt') IS NULL
    ALTER TABLE dbo.Tickets ADD UsedAt DATETIME2 NULL;
GO

/* 2. Payment settlement / refund audit columns. */
IF COL_LENGTH(N'dbo.Payments', N'ConfirmedByAccountId') IS NULL
    ALTER TABLE dbo.Payments ADD ConfirmedByAccountId INT NULL;
IF COL_LENGTH(N'dbo.Payments', N'RefundApprovedByAccountId') IS NULL
    ALTER TABLE dbo.Payments ADD RefundApprovedByAccountId INT NULL;
IF COL_LENGTH(N'dbo.Payments', N'RefundedAt') IS NULL
    ALTER TABLE dbo.Payments ADD RefundedAt DATETIME2 NULL;
IF COL_LENGTH(N'dbo.Payments', N'Notes') IS NULL
    ALTER TABLE dbo.Payments ADD Notes NVARCHAR(500) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Payments_ConfirmedBy')
    ALTER TABLE dbo.Payments ADD CONSTRAINT FK_Payments_ConfirmedBy
        FOREIGN KEY (ConfirmedByAccountId) REFERENCES dbo.Accounts(AccountId);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Payments_RefundApprovedBy')
    ALTER TABLE dbo.Payments ADD CONSTRAINT FK_Payments_RefundApprovedBy
        FOREIGN KEY (RefundApprovedByAccountId) REFERENCES dbo.Accounts(AccountId);
GO

/* 3. Ticket status domain. Legacy databases carry neither CHECK constraint, so
   the settlement state machine could otherwise write an unknown status. The
   constraint is only added when the existing rows already satisfy it. */
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Tickets_Status')
   AND NOT EXISTS (SELECT 1 FROM dbo.Tickets
                   WHERE Status NOT IN (N'Đã đặt', N'Đã thanh toán', N'Đã hủy', N'Đã sử dụng'))
    ALTER TABLE dbo.Tickets ADD CONSTRAINT CK_Tickets_Status
        CHECK (Status IN (N'Đã đặt', N'Đã thanh toán', N'Đã hủy', N'Đã sử dụng'));
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Tickets_PaymentStatus')
   AND NOT EXISTS (SELECT 1 FROM dbo.Tickets
                   WHERE PaymentStatus NOT IN (N'Chưa thanh toán', N'Đã thanh toán', N'Hoàn tiền'))
    ALTER TABLE dbo.Tickets ADD CONSTRAINT CK_Tickets_PaymentStatus
        CHECK (PaymentStatus IN (N'Chưa thanh toán', N'Đã thanh toán', N'Hoàn tiền'));
GO

/* 4. Anti-double-booking index.
   UQ_Trip_Seat is the legacy unfiltered unique constraint on (TripId, SeatId):
   it also blocks re-selling a seat whose ticket was cancelled, so it is
   replaced by the filtered index over the three seat-occupying statuses.
   N'Đã sử dụng' must be part of the filter, otherwise a used ticket's seat can
   be sold again for the same trip. */
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'UQ_Trip_Seat' AND object_id = OBJECT_ID(N'dbo.Tickets') AND is_unique_constraint = 1)
    ALTER TABLE dbo.Tickets DROP CONSTRAINT UQ_Trip_Seat;
GO
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'UQ_Trip_Seat' AND object_id = OBJECT_ID(N'dbo.Tickets'))
    DROP INDEX UQ_Trip_Seat ON dbo.Tickets;
GO
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'UX_Tickets_Active_Trip_Seat' AND object_id = OBJECT_ID(N'dbo.Tickets'))
    DROP INDEX UX_Tickets_Active_Trip_Seat ON dbo.Tickets;
GO
CREATE UNIQUE INDEX UX_Tickets_Active_Trip_Seat ON dbo.Tickets(TripId, SeatId)
WHERE Status IN (N'Đã đặt', N'Đã thanh toán', N'Đã sử dụng');
GO

/* 5. Seat/bus integrity trigger (part of 001_InitialSchema, therefore missing
   on every database that skipped it). */
IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'TR_Tickets_ValidateSeatBus')
    EXEC(N'
CREATE TRIGGER TR_Tickets_ValidateSeatBus ON Tickets AFTER INSERT, UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted i JOIN Trips t ON t.TripId=i.TripId JOIN Seats s ON s.SeatId=i.SeatId
        WHERE t.BusId <> s.BusId
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 51001, ''Seat does not belong to the trip bus.'', 1;
    END
END;');
GO

/* 6. Payment ledger browsing index for the admin payments/report screens. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Payments_Status' AND object_id = OBJECT_ID(N'dbo.Payments'))
    CREATE INDEX IX_Payments_Status ON dbo.Payments(Status, CreatedAt DESC);
GO

/* 7. Expiry sweep support index. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Tickets_PaymentExpiresAt' AND object_id = OBJECT_ID(N'dbo.Tickets'))
    CREATE INDEX IX_Tickets_PaymentExpiresAt ON dbo.Tickets(PaymentExpiresAt)
    WHERE PaymentExpiresAt IS NOT NULL;
GO
