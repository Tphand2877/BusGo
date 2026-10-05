/* Migration 009 – Triggers must not roll back the caller's transaction (AUD-W1-04 / AUD-B-002).

   TR_TicketSeats_AntiDoubleSeat and TR_TicketSeats_ValidateSeatBus both executed
   ROLLBACK TRANSACTION before THROW. That aborts the ambient transaction the
   application opened, so DatabaseHelper.BookTicketsAsync's own
   transaction.RollbackAsync() then throws InvalidOperationException
   ("This SqlTransaction has completed"), which its catch(SqlException) filter does
   not cover. The controlled "One of the selected seats has just been taken" result
   was therefore replaced by an unhandled exception.

   Plain THROW aborts the statement and surfaces the error to the caller, which
   already rolls back. With SET XACT_ABORT ON (used by the T-SQL state machines)
   the transaction is doomed and rolled back by the engine as before.

   Behaviour for valid bookings is unchanged: these triggers only ever fire on
   invalid writes. Migration 008's unique index is now the primary guard; these
   triggers remain as defence in depth and for the seat/bus validation, which no
   index expresses.

   Reversal: re-create both triggers with their previous bodies (recorded in
   Migration_006_MultiSeatTicket.sql:59-88 and :116-132).
*/

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
               WHERE  ts2.SeatId   = i.SeatId
                 AND  tk2.TripId   = tk.TripId
                 AND  tk2.TicketId <> tk.TicketId
                 AND  tk2.Status IN (N'Đã đặt', N'Đã thanh toán', N'Đã sử dụng')
                 AND  (tk2.PaymentStatus = N'Đã thanh toán'
                       OR tk2.PaymentExpiresAt > SYSUTCDATETIME())
               )
    )
    BEGIN
        THROW 51002, 'Another active ticket already holds this seat for the same trip.', 1;
    END
END;
GO

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
        THROW 51001, 'Seat does not belong to the trip bus.', 1;
    END
END;
GO
