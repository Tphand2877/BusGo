/* Payment ledger for the existing BusTicketSaleSystem database. */
USE BusTicketSaleSystem;
GO

IF OBJECT_ID(N'dbo.Payments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Payments
    (
        PaymentId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Payments PRIMARY KEY,
        TicketId INT NOT NULL CONSTRAINT FK_Payments_Tickets REFERENCES dbo.Tickets(TicketId),
        TransactionNo NVARCHAR(100) NULL,
        Provider NVARCHAR(30) NOT NULL CONSTRAINT DF_Payments_Provider DEFAULT N'VNPay',
        ProviderTransactionNo NVARCHAR(100) NULL,
        OrderInfo NVARCHAR(255) NULL,
        PaymentMethod NVARCHAR(50) NOT NULL,
        Amount DECIMAL(18,2) NOT NULL,
        Status NVARCHAR(20) NOT NULL CONSTRAINT DF_Payments_Status DEFAULT N'Pending',
        ResponseCode NVARCHAR(20) NULL,
        VerifiedAt DATETIME2 NULL,
        RawResponse NVARCHAR(MAX) NULL,
        RefundRequestedAt DATETIME2 NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Payments_CreatedAt DEFAULT SYSUTCDATETIME(),
        PaidAt DATETIME2 NULL,
        CONSTRAINT CK_Payments_Method CHECK (PaymentMethod IN (N'VNPay', N'MoMo', N'BankTransfer', N'Cash')),
        CONSTRAINT CK_Payments_Amount CHECK (Amount >= 0),
        CONSTRAINT CK_Payments_Status CHECK (Status IN (N'Pending', N'Success', N'Failed', N'Refunded'))
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Payments_TicketId')
    CREATE INDEX IX_Payments_TicketId ON dbo.Payments(TicketId, CreatedAt DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Payments_TransactionNo')
    CREATE UNIQUE INDEX UX_Payments_TransactionNo ON dbo.Payments(TransactionNo)
    WHERE TransactionNo IS NOT NULL;
GO
