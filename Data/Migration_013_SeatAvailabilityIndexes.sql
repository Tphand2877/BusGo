/* Migration 013 – Indexes for the seat-availability hot path (AUD-W2-11 / AUD-B-003).

   Migration 006 removed the last index whose leading column was Tickets.TripId, and
   TicketSeats has never had one leading on SeatId. Every trip search and every seat-map
   read therefore scans in proportion to total ticket history rather than to the trip
   being looked at:

     DatabaseHelper.cs:252  SearchTripsAsync        — per-trip correlated subquery over TicketSeats x Tickets
     DatabaseHelper.cs:479  GetTripsByCompanyAsync  — same shape
     DatabaseHelper.cs:538  GetSeatsAsync           — per-seat EXISTS filtered by ts.SeatId

   Both indexes are covering for those predicates. No behaviour changes; query results
   are identical.

   Reversal: DROP INDEX IX_Tickets_Trip_Status ON dbo.Tickets;
             DROP INDEX IX_TicketSeats_Seat ON dbo.TicketSeats;
*/

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Tickets_Trip_Status' AND object_id = OBJECT_ID(N'dbo.Tickets'))
    CREATE INDEX IX_Tickets_Trip_Status ON dbo.Tickets (TripId, Status)
        INCLUDE (PaymentStatus, PaymentExpiresAt);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_TicketSeats_Seat' AND object_id = OBJECT_ID(N'dbo.TicketSeats'))
    CREATE INDEX IX_TicketSeats_Seat ON dbo.TicketSeats (SeatId) INCLUDE (TicketId);
GO
