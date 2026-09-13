-- ============================================================================
--  StockDesk / ChewyPetsFeed  —  schema upgrade v2
--  Adds: 3-tier pricing, customer ranking + Tax ID, supplier details,
--        rebates, employees (monthly + loans), waybills, invoice due dates
--        / part-payment, two named warehouses.
--  Safe to run more than once (guards on every change).
-- ============================================================================
USE StockDeskDB;
GO

-- ---- Warehouses: Lawal / Shore ---------------------------------------------
UPDATE Warehouses SET Name = 'Lawal warehouse', Location = 'Lawal site' WHERE Name = 'Main Warehouse';
UPDATE Warehouses SET Name = 'Shore warehouse', Location = 'Shore site' WHERE Name = 'Factory Store';
IF NOT EXISTS (SELECT 1 FROM Warehouses WHERE Name = 'Lawal warehouse')
    INSERT INTO Warehouses (Name, Location) VALUES ('Lawal warehouse', 'Lawal site');
IF NOT EXISTS (SELECT 1 FROM Warehouses WHERE Name = 'Shore warehouse')
    INSERT INTO Warehouses (Name, Location) VALUES ('Shore warehouse', 'Shore site');
GO

-- ---- Products: distributor / wholesaler / retail prices --------------------
IF COL_LENGTH('Products', 'PriceRetail')      IS NULL ALTER TABLE Products ADD PriceRetail      DECIMAL(12,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('Products', 'PriceWholesaler')  IS NULL ALTER TABLE Products ADD PriceWholesaler  DECIMAL(12,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('Products', 'PriceDistributor') IS NULL ALTER TABLE Products ADD PriceDistributor DECIMAL(12,2) NOT NULL DEFAULT 0;
GO
-- Back-fill tiers from the old single SellingPrice (retail = as-is,
-- wholesaler ≈ 3% off, distributor ≈ 7% off — adjust on the Inventory screen).
UPDATE Products SET PriceRetail      = SellingPrice            WHERE PriceRetail      = 0 AND SellingPrice > 0;
UPDATE Products SET PriceWholesaler  = ROUND(SellingPrice*0.97,0) WHERE PriceWholesaler  = 0 AND SellingPrice > 0;
UPDATE Products SET PriceDistributor = ROUND(SellingPrice*0.93,0) WHERE PriceDistributor = 0 AND SellingPrice > 0;
GO

-- ---- Customers: ranking, Tax ID, address, per-customer rebate rate --------
IF COL_LENGTH('Customers', 'CustomerType')  IS NULL ALTER TABLE Customers ADD CustomerType  NVARCHAR(20)  NOT NULL DEFAULT 'Retailer';
IF COL_LENGTH('Customers', 'TaxID')         IS NULL ALTER TABLE Customers ADD TaxID         NVARCHAR(40)  NULL;
IF COL_LENGTH('Customers', 'Address')       IS NULL ALTER TABLE Customers ADD [Address]     NVARCHAR(200) NULL;
IF COL_LENGTH('Customers', 'RebateRatePct') IS NULL ALTER TABLE Customers ADD RebateRatePct DECIMAL(5,2)  NOT NULL DEFAULT 1.0;
GO
UPDATE Customers SET CustomerType = 'Walk-in' WHERE Name LIKE 'Walk-in%';
GO

-- ---- Suppliers: full contact details -------------------------------------
IF COL_LENGTH('Suppliers', 'Address') IS NULL ALTER TABLE Suppliers ADD [Address] NVARCHAR(200) NULL;
IF COL_LENGTH('Suppliers', 'TaxID')   IS NULL ALTER TABLE Suppliers ADD TaxID     NVARCHAR(40)  NULL;
GO

-- ---- Invoices: part-payment, due date, price tier, source warehouse ------
IF COL_LENGTH('Invoices', 'AmountPaid')  IS NULL ALTER TABLE Invoices ADD AmountPaid  DECIMAL(14,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('Invoices', 'DueDate')     IS NULL ALTER TABLE Invoices ADD DueDate     DATE NULL;
IF COL_LENGTH('Invoices', 'PriceTier')   IS NULL ALTER TABLE Invoices ADD PriceTier   NVARCHAR(20) NOT NULL DEFAULT 'Retailer';
IF COL_LENGTH('Invoices', 'WarehouseID') IS NULL ALTER TABLE Invoices ADD WarehouseID INT NULL;
GO
UPDATE Invoices SET AmountPaid = TotalAmount WHERE [Status] = 'Paid' AND AmountPaid = 0;
GO

-- ---- Rebates -------------------------------------------------------------
IF OBJECT_ID('RebateEntries', 'U') IS NULL
CREATE TABLE RebateEntries (
    RebateEntryID INT IDENTITY(1,1) PRIMARY KEY,
    CustomerID    INT NOT NULL REFERENCES Customers(CustomerID),
    InvoiceID     INT NULL REFERENCES Invoices(InvoiceID),
    EntryDate     DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    Amount        DECIMAL(14,2) NOT NULL,
    [Status]      NVARCHAR(12) NOT NULL DEFAULT 'Accrued',  -- Accrued | Redeemed
    RedeemedDate  DATE NULL,
    Note          NVARCHAR(200) NULL
);
GO

-- ---- Employees: monthly roster + loans ---------------------------------
IF OBJECT_ID('Employees', 'U') IS NULL
CREATE TABLE Employees (
    EmployeeID INT IDENTITY(1,1) PRIMARY KEY,
    FullName   NVARCHAR(120) NOT NULL,
    Position   NVARCHAR(80)  NOT NULL,
    Phone      NVARCHAR(30)  NULL,
    StartedOn  DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    IsActive   BIT  NOT NULL DEFAULT 1
);
GO
IF OBJECT_ID('EmployeeMonthly', 'U') IS NULL
CREATE TABLE EmployeeMonthly (
    EmpMonthID    INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeID    INT NOT NULL REFERENCES Employees(EmployeeID),
    PeriodYear    INT NOT NULL,
    PeriodMonth   INT NOT NULL,
    Position      NVARCHAR(80) NOT NULL,
    SalaryAmount  DECIMAL(14,2) NOT NULL DEFAULT 0,
    LoanDeduction DECIMAL(14,2) NOT NULL DEFAULT 0,
    Paid          BIT NOT NULL DEFAULT 0,
    PaidDate      DATE NULL,
    Note          NVARCHAR(200) NULL,
    CONSTRAINT UQ_EmpMonth UNIQUE (EmployeeID, PeriodYear, PeriodMonth)
);
GO
IF OBJECT_ID('EmployeeLoans', 'U') IS NULL
CREATE TABLE EmployeeLoans (
    LoanID     INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeID INT NOT NULL REFERENCES Employees(EmployeeID),
    LoanDate   DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    Principal  DECIMAL(14,2) NOT NULL,
    Balance    DECIMAL(14,2) NOT NULL,
    Note       NVARCHAR(200) NULL,
    Closed     BIT NOT NULL DEFAULT 0
);
GO
IF OBJECT_ID('LoanRepayments', 'U') IS NULL
CREATE TABLE LoanRepayments (
    RepaymentID INT IDENTITY(1,1) PRIMARY KEY,
    LoanID      INT NOT NULL REFERENCES EmployeeLoans(LoanID),
    PaidDate    DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    Amount      DECIMAL(14,2) NOT NULL,
    Note        NVARCHAR(200) NULL
);
GO

-- ---- Waybills ----------------------------------------------------------
IF OBJECT_ID('Waybills', 'U') IS NULL
CREATE TABLE Waybills (
    WaybillID          INT IDENTITY(1,1) PRIMARY KEY,
    WaybillNumber      NVARCHAR(24) NOT NULL UNIQUE,
    InvoiceID          INT NOT NULL REFERENCES Invoices(InvoiceID),
    IssueDate          DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    DriverName         NVARCHAR(120) NULL,
    DriverPhone        NVARCHAR(30)  NULL,
    VehiclePlate       NVARCHAR(20)  NULL,
    DestinationAddress NVARCHAR(250) NULL,
    Notes              NVARCHAR(250) NULL,
    CreatedByUserID    INT NULL
);
GO

-- ---- Settings + seed ------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM AppSettings WHERE SettingKey = 'rebate.ratePct')
    INSERT INTO AppSettings (SettingKey, SettingValue) VALUES ('rebate.ratePct', '1.0');

IF NOT EXISTS (SELECT 1 FROM Employees)
    INSERT INTO Employees (FullName, Position, Phone) VALUES
        ('Ifeoma Chukwu', 'Manager', '0803 000 0001'),
        ('David Okon', 'Warehouse Clerk', '0803 000 0002');
GO

-- ---- Views -------------------------------------------------------------
IF OBJECT_ID('vw_CustomerRebate', 'V') IS NOT NULL DROP VIEW vw_CustomerRebate;
GO
CREATE VIEW vw_CustomerRebate AS
SELECT c.CustomerID, c.Name AS Customer, c.CustomerType,
       ISNULL(SUM(CASE WHEN r.[Status] = 'Accrued'  THEN r.Amount END), 0) AS RebateAvailable,
       ISNULL(SUM(CASE WHEN r.[Status] = 'Redeemed' THEN r.Amount END), 0) AS RebateRedeemed,
       ISNULL(SUM(r.Amount), 0) AS RebateLifetime
FROM Customers c
LEFT JOIN RebateEntries r ON r.CustomerID = c.CustomerID
GROUP BY c.CustomerID, c.Name, c.CustomerType;
GO

IF OBJECT_ID('vw_MonthlyIncome', 'V') IS NOT NULL DROP VIEW vw_MonthlyIncome;
GO
CREATE VIEW vw_MonthlyIncome AS
SELECT YEAR(i.InvoiceDate) AS Yr, MONTH(i.InvoiceDate) AS Mth,
       COUNT(DISTINCT i.InvoiceID) AS Invoices,
       SUM(i.TotalAmount) AS GrossSales,
       SUM(i.VATAmount)   AS VAT,
       SUM(i.TotalAmount - i.VATAmount) AS NetSales,
       SUM(i.AmountPaid)  AS Collected,
       SUM(i.TotalAmount - i.AmountPaid) AS Outstanding
FROM Invoices i
GROUP BY YEAR(i.InvoiceDate), MONTH(i.InvoiceDate);
GO

PRINT 'Migration v2 complete.';
GO
