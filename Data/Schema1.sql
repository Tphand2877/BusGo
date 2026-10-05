/* Fresh schema for BusTicketSaleSystem. Run against a development database. */
IF DB_ID(N'BusTicketSaleSystem') IS NULL CREATE DATABASE BusTicketSaleSystem;
GO
USE BusTicketSaleSystem;
GO

CREATE TABLE Accounts (
    AccountId INT IDENTITY PRIMARY KEY,
    Username NVARCHAR(50) NOT NULL UNIQUE,
    PasswordHash NVARCHAR(255) NOT NULL,
    FullName NVARCHAR(100) NOT NULL,
    Phone NVARCHAR(20) NULL,
    Email NVARCHAR(100) NULL,
    Role NVARCHAR(20) NOT NULL DEFAULT N'Customer',
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Accounts_Role CHECK (Role IN (N'Customer',N'Admin'))
);
GO
CREATE UNIQUE INDEX UX_Accounts_Email ON Accounts(Email) WHERE Email IS NOT NULL;
GO
CREATE TABLE Routes (
    RouteId INT IDENTITY PRIMARY KEY,
    Origin NVARCHAR(100) NOT NULL,
    Destination NVARCHAR(100) NOT NULL,
    DistanceKm DECIMAL(6,1) NULL,
    CONSTRAINT CK_Routes_DifferentStops CHECK (Origin <> Destination)
);
GO
CREATE TABLE Buses (
    BusId INT IDENTITY PRIMARY KEY,
    LicensePlate NVARCHAR(20) NOT NULL UNIQUE,
    SeatCount INT NOT NULL DEFAULT 40 CHECK (SeatCount > 0),
    BusType NVARCHAR(50) NULL
);
GO
CREATE TABLE Trips (
    TripId INT IDENTITY PRIMARY KEY,
    RouteId INT NOT NULL REFERENCES Routes(RouteId),
    BusId INT NOT NULL REFERENCES Buses(BusId),
    DepartureTime DATETIME2 NOT NULL,
    ArrivalTime DATETIME2 NOT NULL,
    Price DECIMAL(10,0) NOT NULL CHECK (Price >= 0),
    CONSTRAINT CK_Trips_TimeOrder CHECK (ArrivalTime > DepartureTime)
);
GO
CREATE TABLE Seats (
    SeatId INT IDENTITY PRIMARY KEY,
    BusId INT NOT NULL REFERENCES Buses(BusId),
    SeatNumber NVARCHAR(10) NOT NULL,
    CONSTRAINT UQ_Seats_Bus_Number UNIQUE (BusId, SeatNumber)
);
GO
CREATE TABLE Tickets (
    TicketId INT IDENTITY PRIMARY KEY,
    TicketCode NVARCHAR(30) NOT NULL UNIQUE,
    TripId INT NOT NULL REFERENCES Trips(TripId),
    SeatId INT NOT NULL REFERENCES Seats(SeatId),
    AccountId INT NOT NULL REFERENCES Accounts(AccountId),
    PassengerName NVARCHAR(100) NOT NULL,
    PassengerPhone NVARCHAR(20) NULL,
    Price DECIMAL(10,0) NOT NULL CHECK (Price >= 0),
    Status NVARCHAR(20) NOT NULL DEFAULT N'Đã đặt',
    PaymentStatus NVARCHAR(20) NOT NULL DEFAULT N'Chưa thanh toán',
    PaymentExpiresAt DATETIME2 NULL,
    BookingTime DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CancelledAt DATETIME2 NULL,
    CONSTRAINT CK_Tickets_Status CHECK (Status IN (N'Đã đặt', N'Đã thanh toán', N'Đã hủy', N'Đã sử dụng')),
    CONSTRAINT CK_Tickets_PaymentStatus CHECK (PaymentStatus IN (N'Chưa thanh toán', N'Đã thanh toán', N'Hoàn tiền'))
);
GO
CREATE UNIQUE INDEX UX_Tickets_Active_Trip_Seat ON Tickets(TripId, SeatId)
WHERE Status IN (N'Đã đặt', N'Đã thanh toán');
GO
/* Ensures a ticket can only use a seat belonging to the trip's bus. */
CREATE TRIGGER TR_Tickets_ValidateSeatBus ON Tickets AFTER INSERT, UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted i JOIN Trips t ON t.TripId=i.TripId JOIN Seats s ON s.SeatId=i.SeatId
        WHERE t.BusId <> s.BusId
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 51001, 'Seat does not belong to the trip bus.', 1;
    END
END;
GO
