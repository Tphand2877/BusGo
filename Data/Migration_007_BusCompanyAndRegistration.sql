/* Migration 007 – Bus Companies, Vehicle Registration & Intermediate Route Stops.
   Adds full terminal registration profile for new buses arriving at the terminal,
   bus companies catalog with ratings and amenities, and waypoints / stops along routes.
*/

/* 1. Bus Companies catalog. */
IF OBJECT_ID(N'dbo.BusCompanies', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BusCompanies
    (
        CompanyId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BusCompanies PRIMARY KEY,
        CompanyName    NVARCHAR(100) NOT NULL CONSTRAINT UQ_BusCompanies_Name UNIQUE,
        Hotline        NVARCHAR(20) NULL,
        Rating         DECIMAL(2,1) NOT NULL CONSTRAINT DF_BusCompanies_Rating DEFAULT 4.8,
        ReviewCount    INT NOT NULL CONSTRAINT DF_BusCompanies_ReviewCount DEFAULT 1250,
        OperatingArea  NVARCHAR(200) NOT NULL CONSTRAINT DF_BusCompanies_Area DEFAULT N'Hà Nội - Thái Nguyên - Lào Cai',
        Description    NVARCHAR(MAX) NULL,
        Amenities      NVARCHAR(500) NULL,
        LogoText       NVARCHAR(10) NOT NULL CONSTRAINT DF_BusCompanies_Logo DEFAULT N'SV',
        BrandColor     NVARCHAR(20) NOT NULL CONSTRAINT DF_BusCompanies_Color DEFAULT N'#0F4C81',
        CreatedAt      DATETIME2 NOT NULL CONSTRAINT DF_BusCompanies_CreatedAt DEFAULT SYSUTCDATETIME()
    );
END;
GO

/* 2. Routes enhancements: Intermediate stops and schedule notes. */
IF COL_LENGTH(N'dbo.Routes', N'IntermediateStops') IS NULL
    ALTER TABLE dbo.Routes ADD IntermediateStops NVARCHAR(500) NULL;
GO
IF COL_LENGTH(N'dbo.Routes', N'DefaultSchedule') IS NULL
    ALTER TABLE dbo.Routes ADD DefaultSchedule NVARCHAR(100) NULL;
GO

/* 3. Buses enhancements: Full registration specifications. */
IF COL_LENGTH(N'dbo.Buses', N'CompanyName') IS NULL
    ALTER TABLE dbo.Buses ADD CompanyName NVARCHAR(100) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'DriverName') IS NULL
    ALTER TABLE dbo.Buses ADD DriverName NVARCHAR(100) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'DriverPhone') IS NULL
    ALTER TABLE dbo.Buses ADD DriverPhone NVARCHAR(20) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'DepartureTimeNote') IS NULL
    ALTER TABLE dbo.Buses ADD DepartureTimeNote NVARCHAR(100) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'RouteId') IS NULL
    ALTER TABLE dbo.Buses ADD RouteId INT NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'IntermediateStops') IS NULL
    ALTER TABLE dbo.Buses ADD IntermediateStops NVARCHAR(500) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'Make') IS NULL
    ALTER TABLE dbo.Buses ADD Make NVARCHAR(50) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'Model') IS NULL
    ALTER TABLE dbo.Buses ADD Model NVARCHAR(50) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'ManufactureYear') IS NULL
    ALTER TABLE dbo.Buses ADD ManufactureYear INT NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'Color') IS NULL
    ALTER TABLE dbo.Buses ADD Color NVARCHAR(50) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'RegistrationNumber') IS NULL
    ALTER TABLE dbo.Buses ADD RegistrationNumber NVARCHAR(50) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'InspectionNumber') IS NULL
    ALTER TABLE dbo.Buses ADD InspectionNumber NVARCHAR(50) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'RegistrationDate') IS NULL
    ALTER TABLE dbo.Buses ADD RegistrationDate DATE NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'InspectionExpiryDate') IS NULL
    ALTER TABLE dbo.Buses ADD InspectionExpiryDate DATE NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'InsuranceExpiryDate') IS NULL
    ALTER TABLE dbo.Buses ADD InsuranceExpiryDate DATE NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'RegisteredEntity') IS NULL
    ALTER TABLE dbo.Buses ADD RegisteredEntity NVARCHAR(150) NULL;
GO
IF COL_LENGTH(N'dbo.Buses', N'OperationType') IS NULL
    ALTER TABLE dbo.Buses ADD OperationType NVARCHAR(50) NULL;
GO

/* 4. Seed initial Bus Companies if empty. */
IF NOT EXISTS (SELECT 1 FROM dbo.BusCompanies WHERE CompanyName = N'Sao Việt')
BEGIN
    INSERT INTO dbo.BusCompanies (CompanyName, Hotline, Rating, ReviewCount, OperatingArea, Description, Amenities, LogoText, BrandColor)
    VALUES (
        N'Sao Việt',
        N'1900 1234',
        4.8,
        1820,
        N'Hà Nội - Thái Nguyên - Lào Cai - Sa Pa',
        N'Nhà xe Sao Việt với hơn 15 năm kinh nghiệm phục vụ hành khách các tuyến phía Bắc. Đội xe Limousine và Giường nằm chất lượng cao, cam kết không bắt khách dọc đường, xuất bến đúng giờ, tài xế chuyên nghiệp và lịch sự.',
        N'Wifi tốc độ cao, Nước uống miễn phí, Khăn lạnh, Cổng sạc USB, Điều hòa hai chiều, Màn hình LCD giải trí, Ghế massage',
        N'SV',
        N'#0F4C81'
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.BusCompanies WHERE CompanyName = N'Hà Lan Buslines')
BEGIN
    INSERT INTO dbo.BusCompanies (CompanyName, Hotline, Rating, ReviewCount, OperatingArea, Description, Amenities, LogoText, BrandColor)
    VALUES (
        N'Hà Lan Buslines',
        N'1900 6868',
        4.9,
        2450,
        N'Hà Nội - Thái Nguyên - Tuyên Quang',
        N'Hà Lan Buslines là đơn vị vận tải hành khách số 1 tuyến Hà Nội - Thái Nguyên. Dịch vụ đưa đón tận nơi trong nội thành, xe chạy liên tục 15 phút/chuyến từ 5h00 đến 21h00 mỗi ngày.',
        N'Wifi 5G, Nước khoáng Lavie, Khăn ướt tiệt trùng, Cổng sạc Type-C/USB, Ghế bọc da VIP, Điều hòa lọc ion',
        N'HL',
        N'#16A34A'
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.BusCompanies WHERE CompanyName = N'Hoàng Long')
BEGIN
    INSERT INTO dbo.BusCompanies (CompanyName, Hotline, Rating, ReviewCount, OperatingArea, Description, Amenities, LogoText, BrandColor)
    VALUES (
        N'Hoàng Long',
        N'1900 7070',
        4.7,
        3120,
        N'Hà Nội - Hải Phòng - Đà Nẵng - TP.HCM',
        N'Công ty TNHH Vận tải Hoàng Long (Hoàng Long Asia) – thương hiệu vận tải đường dài hàng đầu Việt Nam. Dàn xe giường nằm chất lượng cao, phục vụ chuyên nghiệp, an toàn tuyệt đối.',
        N'Wifi miễn phí, Chăn ấm sạch, Nước uống đóng chai, Cổng sạc điện thoại, Rèm che riêng tư, WC trên xe',
        N'HL',
        N'#DC2626'
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.BusCompanies WHERE CompanyName = N'Phương Trang (FUTA)')
BEGIN
    INSERT INTO dbo.BusCompanies (CompanyName, Hotline, Rating, ReviewCount, OperatingArea, Description, Amenities, LogoText, BrandColor)
    VALUES (
        N'Phương Trang (FUTA)',
        N'1900 6067',
        4.8,
        5600,
        N'Toàn quốc: Bắc - Trung - Nam',
        N'FUTA Bus Lines - Chất lượng là danh dự. Hệ thống xe buýt và xe khách liên tỉnh phủ sóng toàn quốc với hàng nghìn chuyến mỗi ngày, trạm dừng chân hiện đại, tiện nghi đồng bộ.',
        N'Wifi tốc độ cao, Nước suối FUTA, Khăn lạnh, Cổng sạc từng ghế, Màn hình riêng, Điều hòa tự động',
        N'FUTA',
        N'#EA580C'
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.BusCompanies WHERE CompanyName = N'Hải Vân Express')
BEGIN
    INSERT INTO dbo.BusCompanies (CompanyName, Hotline, Rating, ReviewCount, OperatingArea, Description, Amenities, LogoText, BrandColor)
    VALUES (
        N'Hải Vân Express',
        N'1900 6763',
        4.8,
        1430,
        N'Hà Nội - Lào Cai - Sa Pa - Điện Biên',
        N'Hải Vân Express chuyên tuyến Tây Bắc với dòng xe Royal và Limousine khoang đôi cao cấp nhất. Trải nghiệm hành trình êm ái như khách sạn di động trên cung đường cao tốc.',
        N'Cabin đơn/đôi riêng biệt, Massage đa điểm, Smart TV kết nối Youtube, Tai nghe khử ồn, Bữa ăn nhẹ miễn phí',
        N'HV',
        N'#7C3AED'
    );
END;
GO

/* 5. Update sample routes with intermediate stops if null. */
UPDATE dbo.Routes
SET IntermediateStops = N'Sân bay Nội Bài, Ngã tư Sóc Sơn, Nút giao Yên Bình, Bến xe Thái Nguyên',
    DefaultSchedule = N'06:00, 08:30, 11:00, 14:00, 16:30, 19:00'
WHERE Origin LIKE N'%Hà Nội%' AND Destination LIKE N'%Thái Nguyên%' AND IntermediateStops IS NULL;
GO

UPDATE dbo.Routes
SET IntermediateStops = N'Nút giao Yên Bình, Ngã tư Sóc Sơn, Sân bay Nội Bài, Bến xe Giáp Bát',
    DefaultSchedule = N'06:30, 09:00, 11:30, 14:30, 17:00, 19:30'
WHERE Origin LIKE N'%Thái Nguyên%' AND Destination LIKE N'%Hà Nội%' AND IntermediateStops IS NULL;
GO

UPDATE dbo.Routes
SET IntermediateStops = N'Nút giao Vĩnh Bảo, Tiên Lãng, Quán Toan, Bến xe Vĩnh Niệm',
    DefaultSchedule = N'06:00, 07:30, 09:00, 11:00, 13:30, 15:30, 18:00'
WHERE Origin LIKE N'%Hà Nội%' AND Destination LIKE N'%Hải Phòng%' AND IntermediateStops IS NULL;
GO

UPDATE dbo.Routes
SET IntermediateStops = N'Phủ Lý, Ninh Bình, Thanh Hóa, Vinh, Hà Tĩnh, Đồng Hới, Huế, Bến xe Trung tâm Đà Nẵng',
    DefaultSchedule = N'08:00, 15:00, 19:00, 21:00'
WHERE Origin LIKE N'%Hà Nội%' AND Destination LIKE N'%Đà Nẵng%' AND IntermediateStops IS NULL;
GO

UPDATE dbo.Routes
SET IntermediateStops = N'Bến xe Miền Đông, Dầu Giây, Phan Thiết, Phan Rang, Cam Ranh, Bến xe Phía Nam Nha Trang',
    DefaultSchedule = N'07:00, 10:00, 13:00, 18:00, 20:30'
WHERE Destination LIKE N'%Nha Trang%' AND IntermediateStops IS NULL;
GO

/* Update other routes with sensible default intermediate stops if null. */
UPDATE dbo.Routes
SET IntermediateStops = Origin + N' (Bến xuất phát), Trạm dừng chân cao tốc, ' + Destination + N' (Bến trả khách)',
    DefaultSchedule = N'07:00, 09:30, 13:30, 16:00, 18:30'
WHERE IntermediateStops IS NULL;
GO

/* 6. Update existing Buses with default registration details if null. */
UPDATE dbo.Buses
SET CompanyName = N'Sao Việt',
    DriverName = N'Nguyễn Văn Hùng',
    DriverPhone = N'0912 345 678',
    DepartureTimeNote = N'06:30, 09:00, 14:30',
    Make = N'Hyundai',
    Model = N'Universe Express',
    ManufactureYear = 2023,
    Color = N'Trắng - Xanh',
    RegistrationNumber = N'29B-99881',
    InspectionNumber = N'KD-7890123',
    RegistrationDate = '2023-05-15',
    InspectionExpiryDate = '2027-05-15',
    InsuranceExpiryDate = '2027-05-15',
    RegisteredEntity = N'Công ty CP Vận tải Sao Việt',
    OperationType = N'Tuyến cố định'
WHERE CompanyName IS NULL AND BusId % 4 = 0;
GO

UPDATE dbo.Buses
SET CompanyName = N'Hà Lan Buslines',
    DriverName = N'Trần Văn Long',
    DriverPhone = N'0988 776 655',
    DepartureTimeNote = N'07:00, 10:00, 15:30',
    Make = N'Thaco',
    Model = N'Mobihome VIP',
    ManufactureYear = 2024,
    Color = N'Xanh lá cây',
    RegistrationNumber = N'20B-55442',
    InspectionNumber = N'KD-8812301',
    RegistrationDate = '2024-02-10',
    InspectionExpiryDate = '2028-02-10',
    InsuranceExpiryDate = '2028-02-10',
    RegisteredEntity = N'Hợp tác xã Vận tải Hà Lan',
    OperationType = N'Tuyến cố định'
WHERE CompanyName IS NULL AND BusId % 4 = 1;
GO

UPDATE dbo.Buses
SET CompanyName = N'Hoàng Long',
    DriverName = N'Lê Hoàng Nam',
    DriverPhone = N'0905 123 987',
    DepartureTimeNote = N'08:00, 13:00, 18:30',
    Make = N'Samco',
    Model = N'Primas Limousine',
    ManufactureYear = 2022,
    Color = N'Trắng - Đỏ',
    RegistrationNumber = N'15B-33211',
    InspectionNumber = N'KD-6543219',
    RegistrationDate = '2022-08-20',
    InspectionExpiryDate = '2026-08-20',
    InsuranceExpiryDate = '2026-08-20',
    RegisteredEntity = N'Công ty TNHH Vận tải Hoàng Long',
    OperationType = N'Tuyến cố định'
WHERE CompanyName IS NULL AND BusId % 4 = 2;
GO

UPDATE dbo.Buses
SET CompanyName = N'Phương Trang (FUTA)',
    DriverName = N'Phạm Minh Tuấn',
    DriverPhone = N'0933 445 566',
    DepartureTimeNote = N'06:00, 11:30, 17:00',
    Make = N'Thaco',
    Model = N'Bluesky 120S',
    ManufactureYear = 2023,
    Color = N'Cam đặc trưng FUTA',
    RegistrationNumber = N'51B-88776',
    InspectionNumber = N'KD-9988771',
    RegistrationDate = '2023-11-05',
    InspectionExpiryDate = '2027-11-05',
    InsuranceExpiryDate = '2027-11-05',
    RegisteredEntity = N'Công ty Cổ phần Xe khách Phương Trang FUTA',
    OperationType = N'Tuyến cố định'
WHERE CompanyName IS NULL;
GO
