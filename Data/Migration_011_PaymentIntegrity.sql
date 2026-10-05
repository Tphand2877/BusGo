/* Migration 011 – One live payment per ticket, and a safe bus delete
   (AUD-W1-10 / AUD-C-002, AUD-W1-12 / AUD-B-005).

   1. CreateCashPaymentAsync and CreateBankTransferPaymentAsync ran their
      "a payment already exists" guard in autocommit, so the UPDLOCK/HOLDLOCK was
      released at statement end and two concurrent calls could both insert a
      Pending row. The application now wraps both batches in a Serializable
      transaction; this index makes the rule an engine invariant so no future
      writer can break it, and maps cleanly onto the existing 51011 message.

   2. Trips had no constraint at all behind the "one bus cannot run two
      overlapping trips" rule, which lived in an unlocked NOT EXISTS. The unique
      index below catches the exact-duplicate slot declaratively; the overlap
      check is additionally taken under HOLDLOCK by the application.

   Both statements abort if existing data violates them, which is the intended
   behaviour: duplicates must be resolved deliberately, never auto-deleted.

   Reversal: DROP INDEX UX_Payments_Live_Ticket ON dbo.Payments;
             DROP INDEX UX_Trips_Bus_Departure ON dbo.Trips;
*/

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Payments_Live_Ticket'
                 AND object_id = OBJECT_ID(N'dbo.Payments'))
    CREATE UNIQUE INDEX UX_Payments_Live_Ticket ON dbo.Payments (TicketId)
    WHERE Status IN (N'Pending', N'Success');
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Trips_Bus_Departure'
                 AND object_id = OBJECT_ID(N'dbo.Trips'))
    CREATE UNIQUE INDEX UX_Trips_Bus_Departure ON dbo.Trips (BusId, DepartureTime);
GO
