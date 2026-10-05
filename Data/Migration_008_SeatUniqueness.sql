/* Migration 008 – Database-enforced seat uniqueness (AUD-W1-03 / AUD-B-001).

   Migration 006 dropped UX_Tickets_Active_Trip_Seat and left the anti-double-sell
   invariant to the Serializable booking transaction plus an AFTER INSERT trigger.
   That combination is isolation-dependent: with READ_COMMITTED_SNAPSHOT ON, two
   concurrent sessions both pass the trigger's EXISTS check against their own row
   version and both commit, so the same seat is sold twice. Reproduced in
   BusTicketSaleSystem.Tests/ConcurrencyBookingTests.cs.

   A unique index cannot span Tickets and TicketSeats directly, so uniqueness is
   declared on a schema-bound view of the active pairs. The unique clustered index
   is maintained by the engine inside every DML transaction and therefore holds
   under any isolation level and for any writer, including direct SQL.

   Filter: the three seat-occupying statuses, matching the original
   UX_Tickets_Active_Trip_Seat definition in Migration_005_Settlement.sql:75-76.
   The expiry predicate used by the trigger cannot appear here (SYSUTCDATETIME is
   nondeterministic); it is not needed, because ExpirePendingPaymentsAsync cancels
   lapsed holds before every booking, which removes them from this filter.

   Reversal: DROP INDEX UX_ActiveTicketSeats_Trip_Seat ON dbo.UX_ActiveTicketSeats;
             DROP VIEW dbo.UX_ActiveTicketSeats;
   Both are metadata-only and destroy no data.
*/

/* 1. Refuse to proceed if the existing data already violates the invariant.
      Data is never deleted or modified here: a violation must be resolved
      deliberately by an operator. */
IF EXISTS (
    SELECT 1
    FROM   dbo.TicketSeats ts
    JOIN   dbo.Tickets tk ON tk.TicketId = ts.TicketId
    WHERE  tk.Status IN (N'Đã đặt', N'Đã thanh toán', N'Đã sử dụng')
    GROUP BY tk.TripId, ts.SeatId
    HAVING COUNT(*) > 1)
BEGIN
    THROW 51020, N'Existing data already contains two active tickets for the same trip and seat. Resolve the duplicates before applying migration 008.', 1;
END;
GO

/* 2. Schema-bound projection of every seat currently occupied on a trip. */
IF OBJECT_ID(N'dbo.UX_ActiveTicketSeats', N'V') IS NULL
    EXEC(N'
CREATE VIEW dbo.UX_ActiveTicketSeats WITH SCHEMABINDING AS
SELECT tk.TripId, ts.SeatId
FROM   dbo.TicketSeats ts
JOIN   dbo.Tickets     tk ON tk.TicketId = ts.TicketId
WHERE  tk.Status IN (N''Đã đặt'', N''Đã thanh toán'', N''Đã sử dụng'');');
GO

/* 3. The invariant itself. Violating writes fail with error 2601 regardless of
      isolation level, transaction style, or whether the application is involved. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_ActiveTicketSeats_Trip_Seat'
                 AND object_id = OBJECT_ID(N'dbo.UX_ActiveTicketSeats'))
    CREATE UNIQUE CLUSTERED INDEX UX_ActiveTicketSeats_Trip_Seat
        ON dbo.UX_ActiveTicketSeats (TripId, SeatId);
GO
