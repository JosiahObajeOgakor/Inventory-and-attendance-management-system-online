Imports System.Data
Imports System.Data.SqlClient
Imports System.Configuration
Imports System.Diagnostics
Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Windows.Forms

''' First-run database setup. The installer bundles SQL Server Express LocalDB
''' (a free, no-install-wizard, single-user database engine — not a separate
''' server anyone has to configure) and this module creates the StockDeskDB
''' database + full schema the first time the app ever runs, straight from the
''' Schema.sql file shipped next to the exe. Every run after that is a no-op.
'''
''' Nothing here touches App.config — it already points at
''' (localdb)\MSSQLLocalDB, which LocalDB auto-provisions the moment anything
''' connects to it, as long as the LocalDB engine itself is installed.
Public Module DbBootstrap

    ''' Where the database files live: %LOCALAPPDATA%\StockDesk\Data unless
    ''' App.config overrides it with DataDir (the tests point it at a temp folder).
    Public Function DataDirectory() As String
        Dim configured = ConfigurationManager.AppSettings("DataDir")
        If Not String.IsNullOrWhiteSpace(configured) Then Return Environment.ExpandEnvironmentVariables(configured)
        Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StockDesk", "Data")
    End Function

    ''' Returns "" on success, or a human-readable error to show the user.
    ''' Sets up the database of the business signed into (Company.Current).
    Public Function EnsureDatabase() As String
        Dim csb As New SqlConnectionStringBuilder(DataAccess.ConnString)
        Dim targetDb = csb.InitialCatalog

        ' LocalDB's named instance can need an explicit create/start on a brand new
        ' PC — this is a no-op (and harmless) once the instance already exists.
        ' If the tool itself can't be found, LocalDB almost certainly isn't
        ' installed at all, which we use below to give a much clearer error.
        Dim localDbToolFound = TryStartLocalDbInstance(csb.DataSource)

        ' Already set up? (Fast path — true on every run after the first.)
        ' Retried a few times: a freshly (re)started LocalDB instance can take a
        ' couple of seconds before it accepts its first connection.
        For attempt = 1 To 3
            Try
                Using conn As New SqlConnection(csb.ConnectionString)
                    conn.Open()
                    Using cmd As New SqlCommand("SELECT OBJECT_ID('dbo.Users', 'U')", conn)
                        If cmd.ExecuteScalar() IsNot DBNull.Value Then Return "" ' Users table exists — done.
                    End Using
                End Using
                Exit For ' connected fine, just no schema yet — fall through to create it
            Catch
                ' Database (or even the instance) doesn't exist yet, or isn't ready
                ' yet — fall through and create it (or retry once more first).
                If attempt < 3 Then Thread.Sleep(1500)
            End Try
        Next

        ' First run: connect to master (always exists) and lay down the schema.
        Dim masterCsb As New SqlConnectionStringBuilder(csb.ConnectionString) With {.InitialCatalog = "master"}
        Dim schemaPath = AppPaths.SchemaFile
        If Not File.Exists(schemaPath) Then
            Return "Schema.sql is missing from the install folder — reinstall ChewyStock."
        End If

        Try
            Using conn As New SqlConnection(masterCsb.ConnectionString)
                conn.Open()

                ' Create the data files in our own per-user app-data folder — never
                ' relies on LocalDB's default placement, so it can't collide with
                ' leftovers from another app/instance and is easy to find/back up.
                Dim dataDir = DataDirectory()
                Directory.CreateDirectory(dataDir)
                Dim mdf = Path.Combine(dataDir, targetDb & ".mdf")
                Dim ldf = Path.Combine(dataDir, targetDb & "_log.ldf")
                Using existsCmd As New SqlCommand("SELECT 1 FROM sys.databases WHERE name = @n", conn)
                    existsCmd.Parameters.AddWithValue("@n", targetDb)
                    If existsCmd.ExecuteScalar() Is Nothing Then
                        Dim create As String
                        If File.Exists(mdf) Then
                            ' Data files survived but the engine forgot them (LocalDB
                            ' reinstalled/reset, or the app reinstalled) — re-attach so
                            ' the customer keeps their data instead of failing on
                            ' "file already exists".
                            create = If(File.Exists(ldf),
                                $"CREATE DATABASE [{targetDb}] ON (FILENAME = N'{mdf}'), (FILENAME = N'{ldf}') FOR ATTACH;",
                                $"CREATE DATABASE [{targetDb}] ON (FILENAME = N'{mdf}') FOR ATTACH_REBUILD_LOG;")
                        Else
                            create = $"CREATE DATABASE [{targetDb}] ON (NAME = N'{targetDb}', FILENAME = N'{mdf}') " &
                                     $"LOG ON (NAME = N'{targetDb}_log', FILENAME = N'{ldf}');"
                        End If
                        Using cmd As New SqlCommand(create, conn) With {.CommandTimeout = 60}
                            cmd.ExecuteNonQuery()
                        End Using
                        ' Connections pooled before the database existed (or while it
                        ' was detached) are stale now — start everyone off fresh.
                        SqlConnection.ClearAllPools()
                    End If
                End Using

                Using hasSchema As New SqlCommand($"SELECT OBJECT_ID(N'[{targetDb}].dbo.Users', 'U')", conn)
                    If hasSchema.ExecuteScalar() IsNot DBNull.Value Then Return ""   ' re-attached an existing database
                End Using

                ' Schema.sql names StockDeskDB (its CREATE/USE header, so it still runs
                ' by hand in SSMS). Point it at the database actually being built —
                ' otherwise a second business's schema lands on top of the first's.
                Dim script = Regex.Replace(File.ReadAllText(schemaPath), "\bStockDeskDB\b", targetDb)
                ' Split on lines that are exactly "GO" (sqlcmd/SSMS batch separator;
                ' not valid inside a single SqlCommand, which only runs one batch).
                For Each batch In Regex.Split(script, "(?im)^\s*GO\s*$")
                    Dim b = batch.Trim()
                    If b = "" Then Continue For
                    Using cmd As New SqlCommand(b, conn) With {.CommandTimeout = 120}
                        cmd.ExecuteNonQuery()
                    End Using
                Next
            End Using
        Catch ex As Exception
            Dim hint = "Make sure SQL Server LocalDB is installed, then restart ChewyStock."
            If Not localDbToolFound Then
                hint = "SQL Server LocalDB does not appear to be installed on this PC." & vbCrLf &
                       "Download and install it from https://go.microsoft.com/fwlink/?linkid=2215160 " &
                       "(or rerun the ChewyStock installer, which offers to do this for you), then restart ChewyStock."
            End If
            Return "Could not set up the database on first run:" & vbCrLf & ex.Message & vbCrLf & vbCrLf & hint
        End Try
        Return ""
    End Function

    ''' Best-effort create+start of the named LocalDB instance via the SqlLocalDB
    ''' command-line tool. Returns False only when the tool itself can't be
    ''' launched — a strong signal LocalDB isn't installed at all — so the caller
    ''' can give a much more specific error than the generic SqlException text.
    ''' No-op (returns True without doing anything) for non-LocalDB connection
    ''' strings, e.g. a real SQL Server in App.config.
    Private Function TryStartLocalDbInstance(dataSource As String) As Boolean
        Dim m = Regex.Match(dataSource, "(?i)^\(localdb\)\\(.+)$")
        If Not m.Success Then Return True
        Dim inst = m.Groups(1).Value
        Dim ranAnything = False
        For Each args In {$"create ""{inst}""", $"start ""{inst}"""}
            Try
                Using p As New Process()
                    p.StartInfo = New ProcessStartInfo("SqlLocalDB.exe", args) With {
                        .UseShellExecute = False, .CreateNoWindow = True,
                        .RedirectStandardOutput = True, .RedirectStandardError = True}
                    p.Start()
                    p.WaitForExit(15000)
                    ranAnything = True
                End Using
            Catch
                ' "create" can legitimately fail if the instance already exists —
                ' only treat "the tool itself is missing" as a real signal.
            End Try
        Next
        Return ranAnything
    End Function

    ''' Small, idempotent catch-up for databases created by an earlier version of
    ''' ChewyStock — EnsureDatabase only lays out the schema once (its fast path
    ''' returns immediately once dbo.Users exists), so a table added after that
    ''' first release needs its own "create if missing" step, run on every start.
    ''' Returns "" on success (including "nothing to do"), else the error text.
    Public Function Migrate() As String
        Try
            DataAccess.Execute(
                "IF OBJECT_ID('dbo.Attendance', 'U') IS NULL " &
                "CREATE TABLE Attendance (" &
                "    AttendanceID INT IDENTITY(1,1) PRIMARY KEY," &
                "    UserID       INT NOT NULL REFERENCES Users(UserID)," &
                "    FullName     NVARCHAR(100) NOT NULL," &
                "    WorkDate     DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE)," &
                "    CheckInAt    DATETIME2 NULL," &
                "    CheckOutAt   DATETIME2 NULL," &
                "    CONSTRAINT UQ_Attendance UNIQUE (UserID, WorkDate)" &
                ");")
            DataAccess.Execute(
                "IF COL_LENGTH('dbo.Employees', 'MonthlySalary') IS NULL " &
                "ALTER TABLE Employees ADD MonthlySalary DECIMAL(14,2) NOT NULL DEFAULT 0;")

            ' Generated demo history is flagged rather than guessed at, so it can
            ' be filtered out of reports and removed again cleanly.
            DataAccess.Execute(
                "IF COL_LENGTH('dbo.Invoices', 'IsSample') IS NULL " &
                "ALTER TABLE Invoices ADD IsSample BIT NOT NULL DEFAULT 0;")
            DataAccess.Execute(
                "IF COL_LENGTH('dbo.PurchaseOrders', 'IsSample') IS NULL " &
                "ALTER TABLE PurchaseOrders ADD IsSample BIT NOT NULL DEFAULT 0;")

            ' Attendance became an append-only log. The old table kept one row
            ' per user per day, so only a day's first check-in was ever stored.
            DataAccess.Execute(
                "IF OBJECT_ID('dbo.AttendanceEvents', 'U') IS NULL " &
                "CREATE TABLE AttendanceEvents (" &
                "    EventID     INT IDENTITY(1,1) PRIMARY KEY," &
                "    UserID      INT NOT NULL REFERENCES Users(UserID)," &
                "    FullName    NVARCHAR(100) NOT NULL," &
                "    EventType   NVARCHAR(10) NOT NULL CHECK (EventType IN ('In','Out','Declined'))," &
                "    HappenedAt  DATETIME2 NOT NULL," &
                "    WorkDate    AS CAST(HappenedAt AS DATE) PERSISTED" &
                ");")
            DataAccess.Execute(
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AttendanceEvents_User_Date' AND object_id=OBJECT_ID('dbo.AttendanceEvents')) " &
                "CREATE INDEX IX_AttendanceEvents_User_Date ON AttendanceEvents(UserID, WorkDate);")
            DataAccess.Execute(
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AttendanceEvents_Date' AND object_id=OBJECT_ID('dbo.AttendanceEvents')) " &
                "CREATE INDEX IX_AttendanceEvents_Date ON AttendanceEvents(WorkDate);")

            ' Carry the old daily rows across so nobody's history disappears.
            ' Only when the log is still empty, so it can't double up on a rerun.
            DataAccess.Execute(
                "IF OBJECT_ID('dbo.Attendance', 'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM AttendanceEvents) " &
                "BEGIN " &
                "  INSERT INTO AttendanceEvents (UserID, FullName, EventType, HappenedAt) " &
                "    SELECT UserID, FullName, 'In', CheckInAt FROM Attendance WHERE CheckInAt IS NOT NULL; " &
                "  INSERT INTO AttendanceEvents (UserID, FullName, EventType, HappenedAt) " &
                "    SELECT UserID, FullName, 'Out', CheckOutAt FROM Attendance WHERE CheckOutAt IS NOT NULL; " &
                "END;")
            ' Document numbers moved from "INV-260908-0001" to "ChewyStock-12092026-143205" —
            ' longer, so the old NVARCHAR(20)/(24) columns need widening first.
            DataAccess.Execute(
                "IF (SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS " &
                "    WHERE TABLE_NAME='Invoices' AND COLUMN_NAME='InvoiceNumber') < 40 " &
                "ALTER TABLE Invoices ALTER COLUMN InvoiceNumber NVARCHAR(40) NOT NULL;")
            DataAccess.Execute(
                "IF (SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS " &
                "    WHERE TABLE_NAME='PurchaseOrders' AND COLUMN_NAME='PONumber') < 40 " &
                "ALTER TABLE PurchaseOrders ALTER COLUMN PONumber NVARCHAR(40) NOT NULL;")
            DataAccess.Execute(
                "IF (SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS " &
                "    WHERE TABLE_NAME='Waybills' AND COLUMN_NAME='WaybillNumber') < 40 " &
                "ALTER TABLE Waybills ALTER COLUMN WaybillNumber NVARCHAR(40) NOT NULL;")
            DataAccess.Execute(
                "IF COL_LENGTH('dbo.Products', 'Barcode') IS NULL " &
                "ALTER TABLE Products ADD Barcode NVARCHAR(64) NULL;")
            DataAccess.Execute(
                "IF COL_LENGTH('dbo.Products', 'TracksSerial') IS NULL " &
                "ALTER TABLE Products ADD TracksSerial BIT NOT NULL DEFAULT 0;")
            ' Filtered unique index: many products may have no barcode yet, but no
            ' two may share one, or a scan at the till would be ambiguous.
            DataAccess.Execute(
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UQ_Products_Barcode' AND object_id=OBJECT_ID('dbo.Products')) " &
                "CREATE UNIQUE INDEX UQ_Products_Barcode ON Products(Barcode) WHERE Barcode IS NOT NULL;")
            DataAccess.Execute(
                "IF OBJECT_ID('dbo.ProductSerials', 'U') IS NULL " &
                "CREATE TABLE ProductSerials (" &
                "    SerialID     INT IDENTITY(1,1) PRIMARY KEY," &
                "    ProductID    INT NOT NULL REFERENCES Products(ProductID)," &
                "    SerialNumber NVARCHAR(80) NOT NULL," &
                "    BatchID      INT NULL REFERENCES StockBatches(BatchID)," &
                "    WarehouseID  INT NULL REFERENCES Warehouses(WarehouseID)," &
                "    Status       NVARCHAR(20) NOT NULL DEFAULT 'In Stock'" &
                "                 CHECK (Status IN ('In Stock','Sold','Returned','Written Off'))," &
                "    ReceivedAt   DATETIME2 NOT NULL DEFAULT SYSDATETIME()," &
                "    SoldAt       DATETIME2 NULL," &
                "    InvoiceID    INT NULL REFERENCES Invoices(InvoiceID)," &
                "    Notes        NVARCHAR(200) NULL," &
                "    CONSTRAINT UQ_ProductSerial UNIQUE (ProductID, SerialNumber)" &
                ");")
            DataAccess.Execute(
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ProductSerials_Status' AND object_id=OBJECT_ID('dbo.ProductSerials')) " &
                "CREATE INDEX IX_ProductSerials_Status ON ProductSerials(ProductID, Status);")

            ' Accounts payable, mirroring the customer side: a running balance
            ' per supplier and how much of each order has actually been paid.
            DataAccess.Execute(
                "IF COL_LENGTH('dbo.Suppliers', 'Balance') IS NULL " &
                "ALTER TABLE Suppliers ADD Balance DECIMAL(14,2) NOT NULL DEFAULT 0;")
            DataAccess.Execute(
                "IF COL_LENGTH('dbo.PurchaseOrders', 'AmountPaid') IS NULL " &
                "ALTER TABLE PurchaseOrders ADD AmountPaid DECIMAL(14,2) NOT NULL DEFAULT 0;")
            ' Older databases marked a PO 'Paid' with no AmountPaid to match —
            ' true it up once so Balance starts from a correct figure below.
            DataAccess.Execute(
                "UPDATE PurchaseOrders SET AmountPaid = TotalAmount WHERE PaymentStatus = 'Paid' AND AmountPaid <> TotalAmount;")
            ' Suppliers.Balance replaces vw_AccountsPayable's old on-the-fly scan;
            ' seed it once from whatever's still outstanding, then Purchasing.Save
            ' and "Mark paid" keep it correct from here on. Only runs while every
            ' supplier is still at the column's own default (0), so it can't
            ' double up on a database that's already been through this.
            DataAccess.Execute(
                "IF NOT EXISTS (SELECT 1 FROM Suppliers WHERE Balance <> 0) " &
                "UPDATE s SET Balance = o.Owed " &
                "FROM Suppliers s JOIN (SELECT SupplierID, SUM(TotalAmount - AmountPaid) AS Owed FROM PurchaseOrders " &
                "                       WHERE PaymentStatus <> 'Paid' GROUP BY SupplierID) o ON o.SupplierID = s.SupplierID;")
            DataAccess.Execute(
                "IF OBJECT_ID('dbo.vw_AccountsPayable', 'V') IS NOT NULL " &
                "EXEC('ALTER VIEW vw_AccountsPayable AS SELECT s.SupplierID, s.Name AS Supplier, s.Balance AS AmountOwed FROM Suppliers s WHERE s.Balance > 0');")

            ' Quotations: a price computation that never touches stock/ledger/
            ' balance until explicitly converted into a real sale.
            DataAccess.Execute(
                "IF OBJECT_ID('dbo.Quotations', 'U') IS NULL " &
                "CREATE TABLE Quotations (" &
                "    QuotationID        INT IDENTITY(1,1) PRIMARY KEY," &
                "    QuotationNumber    NVARCHAR(40) NOT NULL UNIQUE," &
                "    CustomerID         INT NOT NULL REFERENCES Customers(CustomerID)," &
                "    QuotationDate      DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE)," &
                "    Subtotal           DECIMAL(14,2) NOT NULL DEFAULT 0," &
                "    DiscountPct        DECIMAL(5,2)  NOT NULL DEFAULT 0," &
                "    DiscountAmount     DECIMAL(14,2) NOT NULL DEFAULT 0," &
                "    VATRate            DECIMAL(5,2)  NOT NULL DEFAULT 0," &
                "    VATAmount          DECIMAL(14,2) NOT NULL DEFAULT 0," &
                "    TotalAmount        DECIMAL(14,2) NOT NULL DEFAULT 0," &
                "    PriceTier          NVARCHAR(20)  NOT NULL DEFAULT 'Retailer'," &
                "    Status             NVARCHAR(20)  NOT NULL DEFAULT 'Open'," &
                "    ConvertedInvoiceID INT NULL REFERENCES Invoices(InvoiceID)," &
                "    CreatedByUserID    INT NOT NULL REFERENCES Users(UserID)," &
                "    CreatedAt          DATETIME2 NOT NULL DEFAULT SYSDATETIME()" &
                ");")
            DataAccess.Execute(
                "IF OBJECT_ID('dbo.QuotationItems', 'U') IS NULL " &
                "CREATE TABLE QuotationItems (" &
                "    QuotationItemID INT IDENTITY(1,1) PRIMARY KEY," &
                "    QuotationID     INT NOT NULL REFERENCES Quotations(QuotationID)," &
                "    ProductID       INT NOT NULL REFERENCES Products(ProductID)," &
                "    Quantity        INT NOT NULL," &
                "    UnitPrice       DECIMAL(12,2) NOT NULL," &
                "    LineTotal       DECIMAL(14,2) NOT NULL" &
                ");")
            ' Audit trail for manual price overrides on a sale or quotation line.
            ' No FKs to Invoices/Quotations on purpose — either can be deleted,
            ' and this history has to survive that.
            DataAccess.Execute(
                "IF OBJECT_ID('dbo.PriceOverrides', 'U') IS NULL " &
                "CREATE TABLE PriceOverrides (" &
                "    OverrideID    INT IDENTITY(1,1) PRIMARY KEY," &
                "    DocType       NVARCHAR(10) NOT NULL CHECK (DocType IN ('Invoice','Quotation'))," &
                "    DocNumber     NVARCHAR(40) NOT NULL," &
                "    ProductName   NVARCHAR(150) NOT NULL," &
                "    StandardPrice DECIMAL(12,2) NOT NULL," &
                "    OverridePrice DECIMAL(12,2) NOT NULL," &
                "    ChangedByName NVARCHAR(100) NOT NULL," &
                "    ChangedAt     DATETIME2 NOT NULL DEFAULT SYSDATETIME()" &
                ");")
            Return ""
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

    ''' Full native SQL Server backup (.bak) — the file is a complete, restorable
    ''' copy of the database (schema + every row), usable with RESTORE DATABASE on
    ''' any SQL Server/LocalDB instance. Returns "" on success, else the error text.
    Public Function BackupTo(destinationPath As String) As String
        Try
            Dim csb As New SqlConnectionStringBuilder(DataAccess.ConnString)
            Using conn As New SqlConnection(csb.ConnectionString)
                conn.Open()
                Dim sql = $"BACKUP DATABASE [{csb.InitialCatalog}] TO DISK = @path"
                Using cmd As New SqlCommand(sql, conn) With {.CommandTimeout = 180}
                    cmd.Parameters.AddWithValue("@path", destinationPath)
                    cmd.ExecuteNonQuery()
                End Using
            End Using
            Return ""
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

End Module
