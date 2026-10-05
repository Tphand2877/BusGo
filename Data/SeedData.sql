/* Development seed only. This script never deletes application data. */
USE BusTicketSaleSystem;
GO

/* 1. Insert Routes */
IF NOT EXISTS (SELECT 1 FROM Routes WHERE Origin=N'Hà Nội' AND Destination=N'Hải Phòng')
    INSERT INTO Routes (Origin, Destination, DistanceKm) VALUES (N'Hà Nội', N'Hải Phòng', 120);
IF NOT EXISTS (SELECT 1 FROM Routes WHERE Origin=N'Hà Nội' AND Destination=N'Nam Định')
    INSERT INTO Routes (Origin, Destination, DistanceKm) VALUES (N'Hà Nội', N'Nam Định', 90);
IF NOT EXISTS (SELECT 1 FROM Routes WHERE Origin=N'Hà Nội' AND Destination=N'Thái Nguyên')
    INSERT INTO Routes (Origin, Destination, DistanceKm) VALUES (N'Hà Nội', N'Thái Nguyên', 80);
IF NOT EXISTS (SELECT 1 FROM Routes WHERE Origin=N'Hà Nội' AND Destination=N'Thanh Hóa')
    INSERT INTO Routes (Origin, Destination, DistanceKm) VALUES (N'Hà Nội', N'Thanh Hóa', 150);
IF NOT EXISTS (SELECT 1 FROM Routes WHERE Origin=N'Hà Nội' AND Destination=N'Ninh Bình')
    INSERT INTO Routes (Origin, Destination, DistanceKm) VALUES (N'Hà Nội', N'Ninh Bình', 100);
IF NOT EXISTS (SELECT 1 FROM Routes WHERE Origin=N'Hải Phòng' AND Destination=N'Hà Nội')
    INSERT INTO Routes (Origin, Destination, DistanceKm) VALUES (N'Hải Phòng', N'Hà Nội', 120);
IF NOT EXISTS (SELECT 1 FROM Routes WHERE Origin=N'Nam Định' AND Destination=N'Hà Nội')
    INSERT INTO Routes (Origin, Destination, DistanceKm) VALUES (N'Nam Định', N'Hà Nội', 90);
GO

/* 2. Insert Buses & Seats */
IF NOT EXISTS (SELECT 1 FROM Buses WHERE LicensePlate=N'29B-123.45')
    INSERT INTO Buses (LicensePlate, SeatCount, BusType) VALUES (N'29B-123.45', 40, N'Ghế ngồi');

IF NOT EXISTS (SELECT 1 FROM Buses WHERE LicensePlate=N'29B-678.90')
    INSERT INTO Buses (LicensePlate, SeatCount, BusType) VALUES (N'29B-678.90', 40, N'Ghế ngồi cao cấp');

IF NOT EXISTS (SELECT 1 FROM Buses WHERE LicensePlate=N'29B-999.88')
    INSERT INTO Buses (LicensePlate, SeatCount, BusType) VALUES (N'29B-999.88', 34, N'Limousine VIP');

/* Trips are NOT seeded here. A one-time migration can only ever cover the days
   that follow its first run, which left the application with an empty schedule
   a week later. The departure schedule is topped up instead by
   Data/DevSeedTrips.sql, which DatabaseMigrator.EnsureUpcomingTripsAsync runs
   on startup whenever the horizon gets short. */
INSERT INTO Seats (BusId, SeatNumber)
SELECT b.BusId, CONVERT(NVARCHAR(10), n.Number)
FROM Buses b CROSS JOIN (SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) Number FROM sys.objects) n
WHERE n.Number <= b.SeatCount
  AND NOT EXISTS (SELECT 1 FROM Seats s WHERE s.BusId=b.BusId AND s.SeatNumber=CONVERT(NVARCHAR(10), n.Number));
GO
