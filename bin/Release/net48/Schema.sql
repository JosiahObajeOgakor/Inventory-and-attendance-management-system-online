-- StockDesk database schema
-- Target: SQL Server Express / LocalDB
-- Run against a new or existing instance; creates StockDeskDB if missing.

IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'StockDeskDB')
BEGIN
    CREATE DATABASE StockDeskDB;
END
GO
USE StockDeskDB;
GO

-- ===== Reference tables =====

CREATE TABLE Roles (
    RoleID      INT IDENTITY(1,1) PRIMARY KEY,
    RoleName    NVARCHAR(50) NOT NULL UNIQUE
);

CREATE TABLE Categories (
    CategoryID  INT IDENTITY(1,1) PRIMARY KEY,
    Name        NVARCHAR(60) NOT NULL UNIQUE
);

CREATE TABLE Warehouses (
    WarehouseID INT IDENTITY(1,1) PRIMARY KEY,
    Name        NVARCHAR(80) NOT NULL UNIQUE,
    Location    NVARCHAR(150) NULL
);

-- ===== Users / access =====

CREATE TABLE Users (
    UserID        INT IDENTITY(1,1) PRIMARY KEY,
    FullName      NVARCHAR(100) NOT NULL,
    Username      NVARCHAR(50)  NOT NULL UNIQUE,
    PasswordHash  NVARCHAR(256) NOT NULL,   -- PBKDF2 format: pbkdf2$<iterations>$<saltB64>$<hashB64>
    RoleID        INT NOT NULL REFERENCES Roles(RoleID),
    IsActive      BIT NOT NULL DEFAULT 1,
    FailedAttempts     INT NOT NULL DEFAULT 0,
    LockedUntil        DATETIME2 NULL,
    MustChangePassword BIT NOT NULL DEFAULT 0,
    LastLoginAt        DATETIME2 NULL,
    CreatedAt     DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);

CREATE TABLE LoginAudit (
    LoginAuditID INT IDENTITY(1,1) PRIMARY KEY,
    Username     NVARCHAR(50) NOT NULL,
    Succeeded    BIT NOT NULL,
    Reason       NVARCHAR(100) NULL,
    MachineName  NVARCHAR(100) NULL,
    AtUtc        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ===== Products & stock =====

CREATE TABLE Products (
    ProductID     INT IDENTITY(1,1) PRIMARY KEY,
    SKU           NVARCHAR(30)  NOT NULL UNIQUE,
    Name          NVARCHAR(150) NOT NULL,
    CategoryID    INT NOT NULL REFERENCES Categories(CategoryID),
    Unit          NVARCHAR(20)  NOT NULL DEFAULT 'Bag',
    ReorderLevel  INT NOT NULL DEFAULT 0,
    CostPrice     DECIMAL(12,2) NOT NULL DEFAULT 0,
    SellingPrice  DECIMAL(12,2) NOT NULL DEFAULT 0,  -- legacy; kept in sync with PriceRetail
    PriceDistributor DECIMAL(12,2) NOT NULL DEFAULT 0,
    PriceWholesaler  DECIMAL(12,2) NOT NULL DEFAULT 0,
    PriceRetail      DECIMAL(12,2) NOT NULL DEFAULT 0,
    Barcode       NVARCHAR(64) NULL,      -- EAN/UPC from the supplier, or one we mint
    TracksSerial  BIT NOT NULL DEFAULT 0, -- serialised goods (scales, feeders) vs. loose feed
    IsActive      BIT NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX UQ_Products_Barcode ON Products(Barcode) WHERE Barcode IS NOT NULL;

CREATE TABLE StockBatches (
    BatchID       INT IDENTITY(1,1) PRIMARY KEY,
    ProductID     INT NOT NULL REFERENCES Products(ProductID),
    WarehouseID   INT NOT NULL REFERENCES Warehouses(WarehouseID),
    BatchNumber   NVARCHAR(40) NOT NULL,
    ExpiryDate    DATE NULL,
    QuantityOnHand INT NOT NULL DEFAULT 0,
    CONSTRAINT UQ_Batch UNIQUE (ProductID, WarehouseID, BatchNumber)
);

CREATE TABLE StockMovements (
    MovementID    INT IDENTITY(1,1) PRIMARY KEY,
    ProductID     INT NOT NULL REFERENCES Products(ProductID),
    WarehouseID   INT NOT NULL REFERENCES Warehouses(WarehouseID),
    MovementType  NVARCHAR(10) NOT NULL CHECK (MovementType IN ('IN','OUT','ADJUST')),
    Quantity      INT NOT NULL,
    ReferenceType NVARCHAR(20) NULL,   -- 'Invoice', 'PurchaseOrder', 'Manual'
    ReferenceID   INT NULL,
    MovementDate  DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    UserID        INT NOT NULL REFERENCES Users(UserID)
);

-- ===== Suppliers & purchasing =====

CREATE TABLE Suppliers (
    SupplierID   INT IDENTITY(1,1) PRIMARY KEY,
    Name         NVARCHAR(150) NOT NULL,
    Category     NVARCHAR(100) NULL,
    ContactName  NVARCHAR(100) NULL,
    Phone        NVARCHAR(30)  NULL,
    Email        NVARCHAR(100) NULL,
    [Address]    NVARCHAR(200) NULL,
    TaxID        NVARCHAR(40)  NULL
);

CREATE TABLE PurchaseOrders (
    POID         INT IDENTITY(1,1) PRIMARY KEY,
    PONumber     NVARCHAR(40) NOT NULL UNIQUE,
    SupplierID   INT NOT NULL REFERENCES Suppliers(SupplierID),
    OrderDate    DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    Status       NVARCHAR(20) NOT NULL DEFAULT 'Pending', -- Pending, Ordered, Received, Cancelled
    PaymentStatus NVARCHAR(20) NOT NULL DEFAULT 'Unpaid', -- Unpaid, Paid — drives Accounts Payable
    TotalAmount  DECIMAL(14,2) NOT NULL DEFAULT 0,
    CreatedByUserID INT NOT NULL REFERENCES Users(UserID),
    IsSample     BIT NOT NULL DEFAULT 0
);

CREATE TABLE PurchaseOrderItems (
    POItemID     INT IDENTITY(1,1) PRIMARY KEY,
    POID         INT NOT NULL REFERENCES PurchaseOrders(POID),
    ProductID    INT NOT NULL REFERENCES Products(ProductID),
    Quantity     INT NOT NULL,
    UnitCost     DECIMAL(12,2) NOT NULL
);

-- ===== Customers & invoicing =====

CREATE TABLE Customers (
    CustomerID   INT IDENTITY(1,1) PRIMARY KEY,
    Name         NVARCHAR(150) NOT NULL,
    ContactName  NVARCHAR(100) NULL,
    Phone        NVARCHAR(30)  NULL,
    Location     NVARCHAR(150) NULL, -- e.g. "Lagos, Lagos State" — for the year-end incentive metrics
    [Address]    NVARCHAR(200) NULL,
    Email        NVARCHAR(100) NULL,
    CustomerType NVARCHAR(20)  NOT NULL DEFAULT 'Retailer', -- Distributor | Wholesaler | Retailer | Walk-in
    TaxID        NVARCHAR(40)  NULL,
    RebateRatePct DECIMAL(5,2) NOT NULL DEFAULT 1.0,        -- % of each sale accrued as rebate
    CreditLimit  DECIMAL(14,2) NOT NULL DEFAULT 0,
    Balance      DECIMAL(14,2) NOT NULL DEFAULT 0
);

CREATE TABLE Invoices (
    InvoiceID       INT IDENTITY(1,1) PRIMARY KEY,
    InvoiceNumber   NVARCHAR(40) NOT NULL UNIQUE,
    CustomerID      INT NOT NULL REFERENCES Customers(CustomerID),
    InvoiceDate     DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    Subtotal        DECIMAL(14,2) NOT NULL DEFAULT 0,
    DiscountPct     DECIMAL(5,2)  NOT NULL DEFAULT 0,
    DiscountAmount  DECIMAL(14,2) NOT NULL DEFAULT 0,
    VATRate         DECIMAL(5,2)  NOT NULL DEFAULT 7.5,
    VATAmount       DECIMAL(14,2) NOT NULL DEFAULT 0,
    TotalAmount     DECIMAL(14,2) NOT NULL DEFAULT 0,
    PaymentMethod   NVARCHAR(20)  NOT NULL DEFAULT 'Cash', -- Cash, Bank Transfer, Card, Credit
    Status          NVARCHAR(20)  NOT NULL DEFAULT 'Unpaid', -- Paid, Partial, Unpaid
    AmountPaid      DECIMAL(14,2) NOT NULL DEFAULT 0,
    DueDate         DATE NULL,
    PriceTier       NVARCHAR(20)  NOT NULL DEFAULT 'Retailer',
    WarehouseID     INT NULL REFERENCES Warehouses(WarehouseID),
    CreatedByUserID INT NOT NULL REFERENCES Users(UserID),
    CreatedAt       DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    -- Generated demo history, so it can be told apart from real trading and
    -- removed again without guesswork.
    IsSample        BIT NOT NULL DEFAULT 0
);

CREATE TABLE InvoiceItems (
    InvoiceItemID INT IDENTITY(1,1) PRIMARY KEY,
    InvoiceID     INT NOT NULL REFERENCES Invoices(InvoiceID),
    ProductID     INT NOT NULL REFERENCES Products(ProductID),
    Quantity      INT NOT NULL,
    UnitPrice     DECIMAL(12,2) NOT NULL,
    UnitCost      DECIMAL(12,2) NOT NULL DEFAULT 0, -- Products.CostPrice captured AT TIME OF SALE — enables exact COGS/profit per invoice
    LineTotal     DECIMAL(14,2) NOT NULL
);

-- One row per physical serialised unit. Status walks In Stock -> Sold, and can
-- also land on Returned or Written Off; InvoiceID ties a warranty claim back to
-- the sale. Declared after Invoices because it points at it.
CREATE TABLE ProductSerials (
    SerialID     INT IDENTITY(1,1) PRIMARY KEY,
    ProductID    INT NOT NULL REFERENCES Products(ProductID),
    SerialNumber NVARCHAR(80) NOT NULL,
    BatchID      INT NULL REFERENCES StockBatches(BatchID),
    WarehouseID  INT NULL REFERENCES Warehouses(WarehouseID),
    Status       NVARCHAR(20) NOT NULL DEFAULT 'In Stock'
                 CHECK (Status IN ('In Stock','Sold','Returned','Written Off')),
    ReceivedAt   DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    SoldAt       DATETIME2 NULL,
    InvoiceID    INT NULL REFERENCES Invoices(InvoiceID),
    Notes        NVARCHAR(200) NULL,
    CONSTRAINT UQ_ProductSerial UNIQUE (ProductID, SerialNumber)
);
CREATE INDEX IX_ProductSerials_Status ON ProductSerials(ProductID, Status);

CREATE TABLE Expenses (
    ExpenseID       INT IDENTITY(1,1) PRIMARY KEY,
    Category        NVARCHAR(50) NOT NULL, -- Rent, Salaries, Utilities, Logistics, Maintenance, Other
    ExpenseDate     DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    Amount          DECIMAL(14,2) NOT NULL,
    Note            NVARCHAR(200) NULL,
    CreatedByUserID INT NOT NULL REFERENCES Users(UserID)
);

CREATE TABLE Payments (
    PaymentID        INT IDENTITY(1,1) PRIMARY KEY,
    InvoiceID        INT NOT NULL REFERENCES Invoices(InvoiceID),
    PaymentDate      DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    Amount           DECIMAL(14,2) NOT NULL,
    Method           NVARCHAR(20) NOT NULL,
    ReceivedByUserID INT NOT NULL REFERENCES Users(UserID)
);

-- ===== Indexes =====

-- Running credit/debit history for customers (AR) and suppliers (AP), shown on
-- the Finance screen. Customer: Debit = sale on credit (they owe more), Credit =
-- payment received. Supplier: Credit = purchase on credit (we owe more), Debit =
-- payment made. Populated by frmNewInvoice (credit sales), a payment-recording
-- form, frmNewPO (purchases), and the "mark PO paid" action.
CREATE TABLE Ledger (
    LedgerID    INT IDENTITY(1,1) PRIMARY KEY,
    EntryDate   DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    AccountType NVARCHAR(10) NOT NULL, -- Customer, Supplier
    AccountName NVARCHAR(150) NOT NULL,
    EntryType   NVARCHAR(10) NOT NULL, -- Debit, Credit
    Amount      DECIMAL(14,2) NOT NULL,
    Reference   NVARCHAR(30) NULL
);

-- Key/value store for app-wide preferences (currently the Appearance settings:
-- theme.palette, theme.fontSize, theme.density, theme.lines, theme.stripes,
-- theme.boldHeaders). Shared by the whole office so everyone sees one style.
CREATE TABLE AppSettings (
    SettingKey   NVARCHAR(80)  NOT NULL PRIMARY KEY,
    SettingValue NVARCHAR(400) NULL
);

-- ===== Rebates =====
-- 1% (default, per-customer overridable) of every credit-customer sale accrues
-- here as 'Accrued'; the Rebates screen redeems a customer's balance as goods,
-- flipping the rows to 'Redeemed'.
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

-- ===== Employees (monthly roster + loans) =====
CREATE TABLE Employees (
    EmployeeID     INT IDENTITY(1,1) PRIMARY KEY,
    FullName       NVARCHAR(120) NOT NULL,
    Position       NVARCHAR(80)  NOT NULL,
    Phone          NVARCHAR(30)  NULL,
    StartedOn      DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    -- Their normal pay, set when adding/editing the employee. "Generate month"
    -- uses this to fill a brand-new employee's first payroll row (later months
    -- carry forward whatever was actually paid last, so a one-off raise or
    -- deduction doesn't get overwritten) — never defaults to 0 unless left blank.
    MonthlySalary  DECIMAL(14,2) NOT NULL DEFAULT 0,
    IsActive       BIT  NOT NULL DEFAULT 1
);

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

CREATE TABLE EmployeeLoans (
    LoanID     INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeID INT NOT NULL REFERENCES Employees(EmployeeID),
    LoanDate   DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    Principal  DECIMAL(14,2) NOT NULL,
    Balance    DECIMAL(14,2) NOT NULL,
    Note       NVARCHAR(200) NULL,
    Closed     BIT NOT NULL DEFAULT 0
);

CREATE TABLE LoanRepayments (
    RepaymentID INT IDENTITY(1,1) PRIMARY KEY,
    LoanID      INT NOT NULL REFERENCES EmployeeLoans(LoanID),
    PaidDate    DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    Amount      DECIMAL(14,2) NOT NULL,
    Note        NVARCHAR(200) NULL
);

-- ===== Waybills =====
CREATE TABLE Waybills (
    WaybillID          INT IDENTITY(1,1) PRIMARY KEY,
    WaybillNumber      NVARCHAR(40) NOT NULL UNIQUE,
    InvoiceID          INT NOT NULL REFERENCES Invoices(InvoiceID),
    IssueDate          DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
    DriverName         NVARCHAR(120) NULL,
    DriverPhone        NVARCHAR(30)  NULL,
    VehiclePlate       NVARCHAR(20)  NULL,
    DestinationAddress NVARCHAR(250) NULL,
    Notes              NVARCHAR(250) NULL,
    CreatedByUserID    INT NULL
);

-- ===== Attendance =====
-- One row per user per day. Written by the clerk "Checked In" card shown while
-- the welcome clip plays (CheckInAt), and by sign-out/idle-logout (CheckOutAt).
-- Attendance is an append-only log: one row per check-in, check-out or
-- declined check-in, stamped with the moment it happened. Never one row per
-- day -- a clerk who signs in after lunch is checked in again, and both times
-- have to survive. WorkDate is computed from HappenedAt so the date and the
-- time can never drift apart.
CREATE TABLE AttendanceEvents (
    EventID     INT IDENTITY(1,1) PRIMARY KEY,
    UserID      INT NOT NULL REFERENCES Users(UserID),
    FullName    NVARCHAR(100) NOT NULL,
    EventType   NVARCHAR(10) NOT NULL CHECK (EventType IN ('In','Out','Declined')),
    HappenedAt  DATETIME2 NOT NULL,
    WorkDate    AS CAST(HappenedAt AS DATE) PERSISTED
);

CREATE INDEX IX_AttendanceEvents_User_Date ON AttendanceEvents(UserID, WorkDate);
CREATE INDEX IX_AttendanceEvents_Date ON AttendanceEvents(WorkDate);

CREATE INDEX IX_StockBatches_Product ON StockBatches(ProductID);
CREATE INDEX IX_Invoices_Customer ON Invoices(CustomerID);
CREATE INDEX IX_Invoices_Date ON Invoices(InvoiceDate);
CREATE INDEX IX_InvoiceItems_Invoice ON InvoiceItems(InvoiceID);
GO

-- ===== Reporting views =====

CREATE VIEW vw_LowStock AS
SELECT p.ProductID, p.SKU, p.Name, w.Name AS Warehouse, sb.QuantityOnHand, p.ReorderLevel,
       sb.ExpiryDate,
       CASE
         WHEN sb.QuantityOnHand <= p.ReorderLevel THEN 'Low stock'
         WHEN sb.ExpiryDate IS NOT NULL AND DATEDIFF(DAY, GETDATE(), sb.ExpiryDate) <= 60 THEN 'Expiring soon'
         ELSE 'OK'
       END AS Status
FROM StockBatches sb
JOIN Products p ON p.ProductID = sb.ProductID
JOIN Warehouses w ON w.WarehouseID = sb.WarehouseID;
GO

CREATE VIEW vw_StockValuation AS
SELECT p.ProductID, p.SKU, p.Name, SUM(sb.QuantityOnHand) AS TotalQty,
       SUM(sb.QuantityOnHand * p.CostPrice) AS StockValue
FROM StockBatches sb
JOIN Products p ON p.ProductID = sb.ProductID
GROUP BY p.ProductID, p.SKU, p.Name;
GO

-- Exact monthly P&L once InvoiceItems.UnitCost is populated at sale time.
-- Revenue excludes VAT (VATAmount is stored separately on Invoices); Expenses
-- come from the Expenses table. Net profit = GrossProfit - period expenses
-- (join/filter Expenses by the same month in the application/report query).
CREATE VIEW vw_ProfitAndLoss AS
SELECT
    YEAR(i.InvoiceDate) AS Yr, MONTH(i.InvoiceDate) AS Mth,
    SUM(ii.LineTotal) AS Revenue,
    SUM(ii.Quantity * ii.UnitCost) AS COGS,
    SUM(ii.LineTotal) - SUM(ii.Quantity * ii.UnitCost) AS GrossProfit
FROM Invoices i
JOIN InvoiceItems ii ON ii.InvoiceID = i.InvoiceID
GROUP BY YEAR(i.InvoiceDate), MONTH(i.InvoiceDate);
GO

-- Outstanding balance owed TO suppliers (Accounts Payable).
CREATE VIEW vw_AccountsPayable AS
SELECT s.SupplierID, s.Name AS Supplier, SUM(po.TotalAmount) AS AmountOwed
FROM PurchaseOrders po
JOIN Suppliers s ON s.SupplierID = po.SupplierID
WHERE po.PaymentStatus = 'Unpaid'
GROUP BY s.SupplierID, s.Name;
GO

-- Rebate accrued (not yet collected) vs redeemed, per customer.
CREATE VIEW vw_CustomerRebate AS
SELECT c.CustomerID, c.Name AS Customer, c.CustomerType,
       ISNULL(SUM(CASE WHEN r.[Status] = 'Accrued'  THEN r.Amount END), 0) AS RebateAvailable,
       ISNULL(SUM(CASE WHEN r.[Status] = 'Redeemed' THEN r.Amount END), 0) AS RebateRedeemed,
       ISNULL(SUM(r.Amount), 0) AS RebateLifetime
FROM Customers c
LEFT JOIN RebateEntries r ON r.CustomerID = c.CustomerID
GROUP BY c.CustomerID, c.Name, c.CustomerType;
GO

-- Income per month: gross sales, VAT, net sales, cash collected, still outstanding.
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

-- ===== Seed data (matches the approved prototype) =====

INSERT INTO Roles (RoleName) VALUES ('Admin'), ('Warehouse Clerk');

INSERT INTO Categories (Name) VALUES ('Dog Food'), ('Cat Food'), ('Other');

INSERT INTO Warehouses (Name, Location) VALUES ('Lawal warehouse', 'Lawal site'), ('Shore warehouse', 'Shore site');

-- Seed accounts carry a non-verifiable placeholder hash + MustChangePassword=1,
-- so the app forces a real password to be set on the first sign-in. On a brand
-- new database with no usable admin, the app instead runs its "create first
-- admin" screen before login.
INSERT INTO Users (FullName, Username, PasswordHash, RoleID, IsActive, MustChangePassword) VALUES
    ('Ifeoma Chukwu', 'ifeoma.c', 'SETUP_REQUIRED', 1, 1, 1),
    ('David Okon',    'david.o',  'SETUP_REQUIRED', 2, 1, 1);

INSERT INTO Products (SKU, Name, CategoryID, Unit, ReorderLevel, CostPrice, SellingPrice, PriceRetail, PriceWholesaler, PriceDistributor) VALUES
    ('SKU-1001', 'Adult Dog Food 20kg',    1, 'Bag', 30, 8500, 11500, 11500, 11000, 10500),
    ('SKU-1002', 'Puppy Starter 10kg',     1, 'Bag', 25, 6200,  8900,  8900,  8500,  8100),
    ('SKU-1003', 'Cat Food Premium 5kg',   2, 'Bag', 20, 3400,  5200,  5200,  4950,  4700),
    ('SKU-1004', 'Senior Dog Formula 15kg',1, 'Bag', 20, 7100,  9800,  9800,  9400,  9000),
    ('SKU-1005', 'Kitten Formula 3kg',     2, 'Bag', 15, 2600,  4100,  4100,  3900,  3700);

INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, ExpiryDate, QuantityOnHand) VALUES
    (1, 1, 'B24-118', '2026-11-30', 42),
    (2, 1, 'B24-097', '2026-09-20', 18),
    (3, 2, 'B24-102', '2027-01-15', 65),
    (4, 1, 'B24-088', '2026-10-05', 9),
    (5, 2, 'B24-121', '2027-03-01', 30);

INSERT INTO Suppliers (Name, Category, ContactName, Phone, Email, [Address], TaxID) VALUES
    ('AgroFeed Ingredients Ltd', 'Grains & protein meal', 'Bala Sanni', '0802 441 7710', 'sales@agrofeed.example', '14 Mill Road, Kano', 'TIN-1004552'),
    ('PackRight Packaging', 'Bags & labels', 'Nkechi Ude', '0813 220 9981', 'orders@packright.example', '3 Industrial Ave, Lagos', 'TIN-2288140'),
    ('VitaMix Additives Co.', 'Vitamins & supplements', 'Sam Idris', '0906 771 2205', 'info@vitamix.example', '77 Trade Fair Complex, Lagos', 'TIN-3390871');

INSERT INTO Customers (Name, ContactName, Phone, Location, [Address], Email, CustomerType, TaxID, RebateRatePct, CreditLimit, Balance) VALUES
    ('Walk-in Customer', NULL, NULL, NULL, NULL, NULL, 'Walk-in', NULL, 0, 0, 0),
    ('PetMart Lagos', 'Amaka Obi', '0803 555 2210', 'Lagos, Lagos State', '22 Awolowo Rd, Ikoyi, Lagos', 'buyer@petmart.example', 'Distributor', 'TIN-5561200', 1.0, 500000, 120000),
    ('Whiskers & Wag Store', 'Tunde Bello', '0805 221 7745', 'Ibadan, Oyo State', '5 Ring Rd, Ibadan', 'shop@whiskers.example', 'Wholesaler', 'TIN-6672311', 1.0, 300000, 45000),
    ('Companion Pets Abuja', 'Grace Eze', '0701 998 3321', 'Abuja, FCT', '10 Aminu Kano Cres, Wuse 2, Abuja', 'grace@companionpets.example', 'Retailer', 'TIN-7783422', 1.0, 400000, 0);

INSERT INTO Employees (FullName, Position, Phone) VALUES
    ('Ifeoma Chukwu', 'Manager', '0803 000 0001'),
    ('David Okon', 'Warehouse Clerk', '0803 000 0002');

INSERT INTO AppSettings (SettingKey, SettingValue) VALUES
    ('rebate.ratePct', '1.0'),
    ('license.status', 'unactivated');

INSERT INTO Expenses (Category, ExpenseDate, Amount, Note, CreatedByUserID) VALUES
    ('Rent', '2026-09-01', 150000, 'Warehouse site', 1),
    ('Salaries', '2026-09-01', 320000, '2 staff', 1),
    ('Utilities', '2026-09-03', 45000, 'Power & water', 1),
    ('Logistics', '2026-09-06', 60000, 'Delivery fuel', 1);
GO
