USE BusTicketSaleSystem;
GO
IF COL_LENGTH(N'dbo.Tickets', N'PaymentExpiresAt') IS NULL
    ALTER TABLE dbo.Tickets ADD PaymentExpiresAt DATETIME2 NULL;
GO
IF COL_LENGTH(N'dbo.Payments', N'Provider') IS NULL ALTER TABLE dbo.Payments ADD Provider NVARCHAR(30) NOT NULL CONSTRAINT DF_Payments_Provider DEFAULT N'VNPay';
IF COL_LENGTH(N'dbo.Payments', N'ProviderTransactionNo') IS NULL ALTER TABLE dbo.Payments ADD ProviderTransactionNo NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.Payments', N'OrderInfo') IS NULL ALTER TABLE dbo.Payments ADD OrderInfo NVARCHAR(255) NULL;
IF COL_LENGTH(N'dbo.Payments', N'ResponseCode') IS NULL ALTER TABLE dbo.Payments ADD ResponseCode NVARCHAR(20) NULL;
IF COL_LENGTH(N'dbo.Payments', N'VerifiedAt') IS NULL ALTER TABLE dbo.Payments ADD VerifiedAt DATETIME2 NULL;
IF COL_LENGTH(N'dbo.Payments', N'RawResponse') IS NULL ALTER TABLE dbo.Payments ADD RawResponse NVARCHAR(MAX) NULL;
IF COL_LENGTH(N'dbo.Payments', N'RefundRequestedAt') IS NULL ALTER TABLE dbo.Payments ADD RefundRequestedAt DATETIME2 NULL;
GO
UPDATE Tickets SET PaymentExpiresAt = DATEADD(MINUTE, 15, BookingTime)
WHERE Status = N'Đã đặt' AND PaymentStatus = N'Chưa thanh toán' AND PaymentExpiresAt IS NULL;
GO
