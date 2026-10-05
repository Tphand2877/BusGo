/* Additive membership/discount migration. Existing ticket rows receive zero
   discount; seat triggers, payment indexes and historical prices stay intact. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF OBJECT_ID(N'dbo.MembershipTiers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MembershipTiers
    (
        TierCode NVARCHAR(16) NOT NULL CONSTRAINT PK_MembershipTiers PRIMARY KEY,
        MinimumSpend DECIMAL(18,2) NOT NULL,
        CONSTRAINT CK_MembershipTiers_Code CHECK (TierCode IN (N'Standard', N'Bronze', N'Silver', N'Gold', N'Diamond')),
        CONSTRAINT CK_MembershipTiers_MinimumSpend CHECK (MinimumSpend >= 0),
        CONSTRAINT UQ_MembershipTiers_MinimumSpend UNIQUE (MinimumSpend)
    );
END;
GO

INSERT INTO dbo.MembershipTiers (TierCode, MinimumSpend)
SELECT seed.TierCode, seed.MinimumSpend
FROM (VALUES (N'Standard', CONVERT(DECIMAL(18,2), 0)),
             (N'Bronze', 500000), (N'Silver', 2000000),
             (N'Gold', 5000000), (N'Diamond', 10000000)) seed(TierCode, MinimumSpend)
WHERE NOT EXISTS (SELECT 1 FROM dbo.MembershipTiers t WHERE t.TierCode = seed.TierCode);
GO

IF OBJECT_ID(N'dbo.MembershipDiscounts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MembershipDiscounts
    (
        DiscountId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MembershipDiscounts PRIMARY KEY,
        Name NVARCHAR(100) NOT NULL,
        [Percent] DECIMAL(5,2) NOT NULL,
        StartsAt DATETIME2 NOT NULL,
        EndsAt DATETIME2 NOT NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_MembershipDiscounts_IsActive DEFAULT (1),
        CreatedByAccountId INT NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_MembershipDiscounts_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_MembershipDiscounts_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
        CONSTRAINT CK_MembershipDiscounts_Percent CHECK ([Percent] BETWEEN 0.01 AND 99),
        CONSTRAINT CK_MembershipDiscounts_Time CHECK (EndsAt > StartsAt),
        CONSTRAINT FK_MembershipDiscounts_Creator FOREIGN KEY (CreatedByAccountId)
            REFERENCES dbo.Accounts(AccountId) ON DELETE SET NULL
    );
END;
GO

IF OBJECT_ID(N'dbo.MembershipDiscountTiers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MembershipDiscountTiers
    (
        DiscountId INT NOT NULL,
        TierCode NVARCHAR(16) NOT NULL,
        CONSTRAINT PK_MembershipDiscountTiers PRIMARY KEY (DiscountId, TierCode),
        CONSTRAINT CK_MembershipDiscountTiers_Reward CHECK (TierCode <> N'Standard'),
        CONSTRAINT FK_MembershipDiscountTiers_Discount FOREIGN KEY (DiscountId) REFERENCES dbo.MembershipDiscounts(DiscountId),
        CONSTRAINT FK_MembershipDiscountTiers_Tier FOREIGN KEY (TierCode) REFERENCES dbo.MembershipTiers(TierCode)
    );
END;
GO

IF OBJECT_ID(N'dbo.MembershipNotifications', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MembershipNotifications
    (
        NotificationId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MembershipNotifications PRIMARY KEY,
        AccountId INT NOT NULL,
        DiscountId INT NOT NULL,
        TierCode NVARCHAR(16) NOT NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_MembershipNotifications_CreatedAt DEFAULT SYSUTCDATETIME(),
        ReadAt DATETIME2 NULL,
        CONSTRAINT UQ_MembershipNotifications_AccountDiscount UNIQUE (AccountId, DiscountId),
        CONSTRAINT FK_MembershipNotifications_Account FOREIGN KEY (AccountId) REFERENCES dbo.Accounts(AccountId) ON DELETE CASCADE,
        CONSTRAINT FK_MembershipNotifications_Discount FOREIGN KEY (DiscountId) REFERENCES dbo.MembershipDiscounts(DiscountId),
        CONSTRAINT FK_MembershipNotifications_Tier FOREIGN KEY (TierCode) REFERENCES dbo.MembershipTiers(TierCode)
    );
END;
GO

IF COL_LENGTH(N'dbo.Tickets', N'DiscountAmount') IS NULL
    ALTER TABLE dbo.Tickets ADD DiscountAmount DECIMAL(18,2) NOT NULL
        CONSTRAINT DF_Tickets_DiscountAmount DEFAULT (0) WITH VALUES;
IF COL_LENGTH(N'dbo.Tickets', N'MembershipDiscountId') IS NULL
    ALTER TABLE dbo.Tickets ADD MembershipDiscountId INT NULL;
IF COL_LENGTH(N'dbo.Tickets', N'DiscountName') IS NULL
    ALTER TABLE dbo.Tickets ADD DiscountName NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.Tickets', N'MembershipTier') IS NULL
    ALTER TABLE dbo.Tickets ADD MembershipTier NVARCHAR(16) NULL;
GO

IF COL_LENGTH(N'dbo.Tickets', N'TotalAmount') IS NULL
    ALTER TABLE dbo.Tickets ADD TotalAmount AS CONVERT(DECIMAL(18,2), Price * SeatCount - DiscountAmount) PERSISTED;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Tickets_DiscountAmount' AND parent_object_id = OBJECT_ID(N'dbo.Tickets'))
    ALTER TABLE dbo.Tickets WITH CHECK ADD CONSTRAINT CK_Tickets_DiscountAmount
        CHECK (DiscountAmount >= 0 AND DiscountAmount <= Price * SeatCount);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Tickets_MembershipDiscount')
    ALTER TABLE dbo.Tickets WITH CHECK ADD CONSTRAINT FK_Tickets_MembershipDiscount
        FOREIGN KEY (MembershipDiscountId) REFERENCES dbo.MembershipDiscounts(DiscountId);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Tickets_MembershipTier')
    ALTER TABLE dbo.Tickets WITH CHECK ADD CONSTRAINT FK_Tickets_MembershipTier
        FOREIGN KEY (MembershipTier) REFERENCES dbo.MembershipTiers(TierCode);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MembershipDiscountTiers_Tier' AND object_id = OBJECT_ID(N'dbo.MembershipDiscountTiers'))
    CREATE INDEX IX_MembershipDiscountTiers_Tier ON dbo.MembershipDiscountTiers(TierCode, DiscountId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MembershipDiscounts_ActiveEnd' AND object_id = OBJECT_ID(N'dbo.MembershipDiscounts'))
    CREATE INDEX IX_MembershipDiscounts_ActiveEnd ON dbo.MembershipDiscounts(EndsAt, StartsAt) INCLUDE ([Percent], Name) WHERE IsActive = 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MembershipNotifications_AccountCreated' AND object_id = OBJECT_ID(N'dbo.MembershipNotifications'))
    CREATE INDEX IX_MembershipNotifications_AccountCreated ON dbo.MembershipNotifications(AccountId, CreatedAt DESC, NotificationId DESC)
        INCLUDE (DiscountId, TierCode, ReadAt);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MembershipNotifications_Unread' AND object_id = OBJECT_ID(N'dbo.MembershipNotifications'))
    CREATE INDEX IX_MembershipNotifications_Unread ON dbo.MembershipNotifications(AccountId) WHERE ReadAt IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Tickets_MembershipSpend' AND object_id = OBJECT_ID(N'dbo.Tickets'))
    CREATE INDEX IX_Tickets_MembershipSpend ON dbo.Tickets(AccountId) INCLUDE (TotalAmount) WHERE PaymentStatus = N'Đã thanh toán';
GO

CREATE OR ALTER VIEW dbo.CustomerMemberships
AS
SELECT a.AccountId, spend.QualifiedSpend, tier.TierCode, tier.MinimumSpend
FROM dbo.Accounts a
CROSS APPLY
(
    SELECT COALESCE(SUM(t.TotalAmount), CONVERT(DECIMAL(38,2), 0)) AS QualifiedSpend
    FROM dbo.Tickets t
    WHERE t.AccountId = a.AccountId AND t.PaymentStatus = N'Đã thanh toán'
) spend
CROSS APPLY
(
    SELECT TOP (1) mt.TierCode, mt.MinimumSpend
    FROM dbo.MembershipTiers mt
    WHERE mt.MinimumSpend <= spend.QualifiedSpend
    ORDER BY mt.MinimumSpend DESC
) tier
WHERE a.Role = N'Customer';
GO
