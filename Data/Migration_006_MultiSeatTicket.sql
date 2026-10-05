/* Migration 006 – Multi-seat tickets.
   A single Tickets row now represents the whole booking (one code, one
   payment, one passenger record). Seats are stored in a new TicketSeats
   junction table so the ticket view shows "Seats: 1, 2, 5, 6" instead
   of creating four separate tickets.

   Backward-compatible: existing one-seat tickets are migrated into
   TicketSeats automatically.  SeatId on Tickets becomes nullable and
   deprecated; new code never writes it.
*/

/* 1. Create junction table. */
IF OBJECT_ID(N'dbo.TicketSeats', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TicketSeats
    (
        TicketSeatId INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_TicketSeats PRIMARY KEY,
        TicketId     INT NOT NULL
            CONSTRAINT FK_TicketSeats_Ticket FOREIGN KEY REFERENCES dbo.Tickets(TicketId),
        SeatId       INT NOT NULL
            CONSTRAINT FK_TicketSeats_Seat   FOREIGN KEY REFERENCES dbo.Seats(SeatId),
        CONSTRAINT UQ_TicketSeats_TicketSeat UNIQUE (TicketId, SeatId)
    );
END;
GO

/* 2. Migrate every existing Tickets.SeatId into the junction table. */
INSERT INTO dbo.TicketSeats (TicketId, SeatId)
SELECT t.TicketId, t.SeatId
FROM   dbo.Tickets t
WHERE  t.SeatId IS NOT NULL
  AND  NOT EXISTS (SELECT 1 FROM dbo.TicketSeats ts
                   WHERE ts.TicketId = t.TicketId AND ts.SeatId = t.SeatId);
GO

/* 3. Add SeatCount column (always 1 for legacy rows). */
IF COL_LENGTH(N'dbo.Tickets', N'SeatCount') IS NULL
    ALTER TABLE dbo.Tickets ADD SeatCount INT NOT NULL
        CONSTRAINT DF_Tickets_SeatCount DEFAULT 1;
GO

/* 4. Drop the old anti-double-booking index that lives on Tickets.SeatId.
      Protection now comes from the Serializable transaction + NOT EXISTS
      against TicketSeats. Migration_005 also created a version of this index;
      drop whichever name exists. */
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Tickets_Active_Trip_Seat'
           AND object_id = OBJECT_ID(N'dbo.Tickets'))
    DROP INDEX UX_Tickets_Active_Trip_Seat ON dbo.Tickets;
GO

/* 5. Belt-and-suspenders: prevent two active tickets from holding the same
      seat on the same trip via TicketSeats. We cannot use a simple filtered
      unique index across two tables, so we create a trigger that raises on
      conflict. The Serializable transaction remains the primary guard. */
IF OBJECT_ID(N'dbo.TR_TicketSeats_AntiDoubleSeat', N'TR') IS NOT NULL
    DROP TRIGGER dbo.TR_TicketSeats_AntiDoubleSeat;
GO
CREATE TRIGGER dbo.TR_TicketSeats_AntiDoubleSeat
ON dbo.TicketSeats AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM   inserted i
        JOIN   dbo.Tickets tk ON tk.TicketId = i.TicketId
        WHERE  tk.Status IN (N'Đã đặt', N'Đã thanh toán', N'Đã sử dụng')
          AND  EXISTS (
               SELECT 1
               FROM   dbo.TicketSeats ts2
               JOIN   dbo.Tickets tk2 ON tk2.TicketId = ts2.TicketId
               WHERE  ts2.SeatId  = i.SeatId
                 AND  tk2.TripId  = tk.TripId
                 AND  tk2.TicketId <> tk.TicketId
                 AND  tk2.Status IN (N'Đã đặt', N'Đã thanh toán', N'Đã sử dụng')
                 AND  (tk2.PaymentStatus = N'Đã thanh toán'
                       OR tk2.PaymentExpiresAt > SYSUTCDATETIME())
               )
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 51002, 'Another active ticket already holds this seat for the same trip.', 1;
    END
END;
GO

/* 6. Make Tickets.SeatId nullable for new multi-seat bookings.
      Existing rows keep their value; new rows will have NULL.
      Must drop the FK, the old trigger, alter, then re-add the FK. */

/* Drop the seat-bus trigger (it references Tickets.SeatId which is being deprecated). */
IF OBJECT_ID(N'dbo.TR_Tickets_ValidateSeatBus', N'TR') IS NOT NULL
    DROP TRIGGER dbo.TR_Tickets_ValidateSeatBus;
GO

/* Drop FK on Tickets.SeatId if it exists. */
DECLARE @fk NVARCHAR(256);
SELECT @fk = fk.name
FROM   sys.foreign_keys fk
JOIN   sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN   sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
WHERE  fk.parent_object_id = OBJECT_ID(N'dbo.Tickets')
  AND  c.name = N'SeatId';
IF @fk IS NOT NULL
    EXEC(N'ALTER TABLE dbo.Tickets DROP CONSTRAINT [' + @fk + N'];');
GO

/* Make SeatId nullable. */
ALTER TABLE dbo.Tickets ALTER COLUMN SeatId INT NULL;
GO

/* Re-create seat-bus validation as a trigger on TicketSeats instead. */
IF OBJECT_ID(N'dbo.TR_TicketSeats_ValidateSeatBus', N'TR') IS NOT NULL
    DROP TRIGGER dbo.TR_TicketSeats_ValidateSeatBus;
GO
CREATE TRIGGER dbo.TR_TicketSeats_ValidateSeatBus
ON dbo.TicketSeats AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM   inserted i
        JOIN   dbo.Tickets tk ON tk.TicketId = i.TicketId
        JOIN   dbo.Trips   tr ON tr.TripId   = tk.TripId
        JOIN   dbo.Seats   s  ON s.SeatId    = i.SeatId
        WHERE  tr.BusId <> s.BusId
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 51001, 'Seat does not belong to the trip bus.', 1;
    END
END;
GO
