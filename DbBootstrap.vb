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
    Public Function EnsureDatabase() As String
        Dim csb As New SqlConnectionStringBuilder(ConfigurationManager.ConnectionStrings("StockDeskDB").ConnectionString)
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

                Dim script = File.ReadAllText(schemaPath)
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
            Dim csb As New SqlConnectionStringBuilder(ConfigurationManager.ConnectionStrings("StockDeskDB").ConnectionString)
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
