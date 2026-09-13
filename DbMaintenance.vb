Imports System.Data
Imports System.Data.SqlClient
Imports System.Configuration
Imports System.IO
Imports System.Text
Imports System.Windows.Forms
Imports ClosedXML.Excel

''' Database storage: size monitoring + "archive old records".
'''
''' LocalDB (SQL Server Express) caps a database at 10 GB. When usage nears the
''' cap, admins are prompted to back up and archive. Archiving copies old,
''' SETTLED records to Excel + CSV files the user keeps, then deletes them —
''' all inside one transaction, so if the files can't be written nothing is
''' deleted. Customer/supplier balances, stock levels and loan balances are
''' stored columns, not sums of history, so archiving history never changes them.
Public Module DbMaintenance

    Public Class Usage
        Public Property UsedMB As Double
        Public Property LimitMB As Double   ' 0 = no known limit
        Public ReadOnly Property Percent As Double
            Get
                Return If(LimitMB > 0, UsedMB / LimitMB * 100, 0)
            End Get
        End Property
    End Class

    Public Function GetUsage() As Usage
        Dim u As New Usage()
        Dim t = DataAccess.GetTable(
            "SELECT CAST(SUM(CAST(FILEPROPERTY(name, 'SpaceUsed') AS BIGINT)) * 8 / 1024.0 AS FLOAT) AS UsedMB, " &
            "CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition " &
            "FROM sys.database_files WHERE type = 0")
        u.UsedMB = Convert.ToDouble(t.Rows(0)("UsedMB"))
        Dim configured = 0.0
        Double.TryParse(ConfigurationManager.AppSettings("DbSizeLimitMB"), configured)
        If configured > 0 Then
            u.LimitMB = configured
        ElseIf Convert.ToInt32(t.Rows(0)("Edition")) = 4 Then   ' 4 = Express (incl. LocalDB)
            u.LimitMB = 10240
        End If
        Return u
    End Function

    Private Function WarnPercent() As Double
        Dim p = 80.0
        Double.TryParse(ConfigurationManager.AppSettings("DbSizeWarnPercent"), p)
        Return If(p <= 0 OrElse p > 100, 80, p)
    End Function

    Private _checkedThisRun As Boolean

    ''' Called once per app run after sign-in. Admins get the archive prompt;
    ''' clerks are told to ask an admin.
    Public Sub CheckOnLogin(owner As Form, isAdmin As Boolean)
        If _checkedThisRun Then Return
        _checkedThisRun = True
        Dim u As Usage
        Try
            u = GetUsage()
        Catch
            Return
        End Try
        If u.LimitMB <= 0 OrElse u.Percent < WarnPercent() Then Return

        Dim msg = $"The database is {u.Percent:0}% full ({u.UsedMB:#,0} MB of {u.LimitMB:#,0} MB)." & vbCrLf & vbCrLf &
                  "When it is full, new sales and records can't be saved."
        If Not isAdmin Then
            AppUI.Info(owner, msg & " Ask an administrator to archive old records.", "Database almost full")
            Return
        End If
        If AppUI.Confirm(owner, msg & " Back up now and archive old records to Excel/CSV?",
                         "Database almost full", "Back up and archive…") Then
            Using f As New frmArchive()
                f.ShowDialog(owner)
            End Using
        End If
    End Sub

    ' ===== "Database is full" errors raised by any save =====

    Public Function IsFullError(ex As SqlException) As Boolean
        For Each er As SqlError In ex.Errors
            ' 1101/1105: filegroup full; 1827/1828: Express 10 GB licence cap.
            If er.Number = 1101 OrElse er.Number = 1105 OrElse er.Number = 1827 OrElse er.Number = 1828 Then Return True
        Next
        Return False
    End Function

    Private _lastFullNotice As DateTime = DateTime.MinValue

    Public Sub NotifyFull()
        If (DateTime.Now - _lastFullNotice).TotalSeconds < 60 Then Return
        _lastFullNotice = DateTime.Now
        Dim owner = If(Application.OpenForms.Count > 0, Application.OpenForms(Application.OpenForms.Count - 1), Nothing)
        AppUI.Info(owner,
            "The database is full, so this couldn't be saved." & vbCrLf & vbCrLf &
            "An administrator should open Account ▸ Database storage and archive, back up, and archive old records.",
            "Database full")
    End Sub

    ' ===== Archive =====

    ' Settled invoices / purchase orders / closed loans dated before the cutoff.
    ' Unpaid or part-paid invoices, unpaid POs, open loans and unredeemed
    ' rebates are always kept, whatever their age.
    Private Const InvSel As String =
        "SELECT InvoiceID FROM Invoices WHERE InvoiceDate < @cut AND ([Status] = 'Paid' OR AmountPaid >= TotalAmount)"
    Private Const PoSel As String =
        "SELECT POID FROM PurchaseOrders WHERE OrderDate < @cut AND PaymentStatus = 'Paid' AND [Status] IN ('Received', 'Cancelled')"
    Private Const LoanSel As String =
        "SELECT l.LoanID FROM EmployeeLoans l WHERE l.Closed = 1 AND l.LoanDate < @cut " &
        "AND NOT EXISTS (SELECT 1 FROM LoanRepayments r WHERE r.LoanID = l.LoanID AND r.PaidDate >= @cut)"

    ' In DELETE order (children before parents).
    Private ReadOnly Specs As (Table As String, Where As String)() = {
        ("InvoiceItems", "InvoiceID IN (" & InvSel & ")"),
        ("Payments", "InvoiceID IN (" & InvSel & ")"),
        ("Waybills", "InvoiceID IN (" & InvSel & ")"),
        ("RebateEntries", "[Status] = 'Redeemed' AND EntryDate < @cut"),
        ("Invoices", "InvoiceID IN (" & InvSel & ")"),
        ("PurchaseOrderItems", "POID IN (" & PoSel & ")"),
        ("PurchaseOrders", "POID IN (" & PoSel & ")"),
        ("Expenses", "ExpenseDate < @cut"),
        ("Ledger", "EntryDate < @cut"),
        ("StockMovements", "MovementDate < @cut"),
        ("EmployeeMonthly", "Paid = 1 AND DATEFROMPARTS(PeriodYear, PeriodMonth, 1) < @cut"),
        ("LoanRepayments", "LoanID IN (" & LoanSel & ")"),
        ("EmployeeLoans", "LoanID IN (" & LoanSel & ")"),
        ("LoginAudit", "AtUtc < @cut")
    }

    ' Accrued (unredeemed) rebates stay, but lose the link to an archived invoice.
    Private Const DetachRebates As String =
        "UPDATE RebateEntries SET InvoiceID = NULL, " &
        "Note = LEFT(ISNULL(Note + ' ', '') + '(invoice ' + (SELECT InvoiceNumber FROM Invoices i WHERE i.InvoiceID = RebateEntries.InvoiceID) + ' archived)', 200) " &
        "WHERE InvoiceID IN (" & InvSel & ")"

    ' ClosedXML holds a workbook in memory; bigger tables go to CSV only.
    Private Const XlsxRowLimit As Integer = 200000

    ''' Records that WOULD be archived for this cutoff, per table.
    Public Function Preview(cutoff As Date) As List(Of (Table As String, Rows As Integer))
        Dim result As New List(Of (Table As String, Rows As Integer))
        Dim p As New Dictionary(Of String, Object) From {{"@cut", cutoff.Date}}
        For Each s In Specs
            Dim t = DataAccess.GetTable($"SELECT COUNT(*) FROM {s.Table} WHERE {s.Where}", p)
            result.Add((s.Table, Convert.ToInt32(t.Rows(0)(0))))
        Next
        Return result
    End Function

    ''' Writes every matching record to <folder>\Archive.xlsx and <folder>\csv\*.csv,
    ''' then deletes them — one transaction; any failure rolls everything back.
    ''' Returns the number of records archived.
    Public Function Archive(cutoff As Date, folder As String) As Integer
        Dim csvDir = Path.Combine(folder, "csv")
        Directory.CreateDirectory(csvDir)
        Dim total = 0

        Using conn As New SqlConnection(ConfigurationManager.ConnectionStrings("StockDeskDB").ConnectionString)
            conn.Open()
            Using tx = conn.BeginTransaction(IsolationLevel.Serializable)
                Try
                    ' 1. Export (in a readable order: parents first).
                    Using wb As New XLWorkbook()
                        For Each s In Enumerable.Reverse(Specs)
                            Dim sql = $"SELECT * FROM {s.Table} WHERE {s.Where}"
                            Dim rows = WriteCsv(conn, tx, sql, cutoff, Path.Combine(csvDir, s.Table & ".csv"))
                            total += rows
                            Dim ws = wb.Worksheets.Add(s.Table)
                            If rows = 0 Then
                                ws.Cell(1, 1).Value = "(nothing archived)"
                            ElseIf rows > XlsxRowLimit Then
                                ws.Cell(1, 1).Value = $"{rows:#,0} rows — too many for one sheet; see csv\{s.Table}.csv"
                            Else
                                ws.Cell(1, 1).InsertTable(Load(conn, tx, sql, cutoff), s.Table)
                                ws.Columns().AdjustToContents(1, 200)
                            End If
                        Next
                        Dim info = wb.Worksheets.Add("About", 1)
                        info.Cell(1, 1).Value = "ChewyStock archive"
                        info.Cell(2, 1).Value = $"Settled records dated before {cutoff:yyyy-MM-dd}, removed from the database on {DateTime.Now:yyyy-MM-dd HH:mm}."
                        info.Cell(3, 1).Value = $"{total:#,0} records in total. Full copies are in the csv folder next to this file."
                        wb.SaveAs(Path.Combine(folder, "Archive.xlsx"))
                    End Using

                    ' 2. Delete (children first).
                    Exec(conn, tx, DetachRebates, cutoff)
                    For Each s In Specs
                        Exec(conn, tx, $"DELETE FROM {s.Table} WHERE {s.Where}", cutoff)
                    Next
                    tx.Commit()
                Catch
                    tx.Rollback()
                    Throw
                End Try
            End Using
        End Using
        Return total
    End Function

    Private Function NewCmd(conn As SqlConnection, tx As SqlTransaction, sql As String, cutoff As Date) As SqlCommand
        Dim cmd As New SqlCommand(sql, conn, tx) With {.CommandTimeout = 0}
        cmd.Parameters.Add("@cut", SqlDbType.Date).Value = cutoff.Date
        Return cmd
    End Function

    Private Sub Exec(conn As SqlConnection, tx As SqlTransaction, sql As String, cutoff As Date)
        Using cmd = NewCmd(conn, tx, sql, cutoff)
            cmd.ExecuteNonQuery()
        End Using
    End Sub

    Private Function Load(conn As SqlConnection, tx As SqlTransaction, sql As String, cutoff As Date) As DataTable
        Using cmd = NewCmd(conn, tx, sql, cutoff)
            Dim t As New DataTable()
            t.Load(cmd.ExecuteReader())
            Return t
        End Using
    End Function

    ''' Streams rows straight to disk (no DataTable), so huge tables are fine.
    Private Function WriteCsv(conn As SqlConnection, tx As SqlTransaction, sql As String, cutoff As Date, file As String) As Integer
        Dim rows = 0
        Using cmd = NewCmd(conn, tx, sql, cutoff)
            Using rd = cmd.ExecuteReader()
                Using w As New StreamWriter(file, False, New UTF8Encoding(True))
                    Dim names As New List(Of String)
                    For i = 0 To rd.FieldCount - 1
                        names.Add(AppUI.CsvField(rd.GetName(i)))
                    Next
                    w.WriteLine(String.Join(",", names))
                    While rd.Read()
                        Dim vals(rd.FieldCount - 1) As String
                        For i = 0 To rd.FieldCount - 1
                            vals(i) = AppUI.CsvField(FormatValue(rd.GetValue(i)))
                        Next
                        w.WriteLine(String.Join(",", vals))
                        rows += 1
                    End While
                End Using
            End Using
        End Using
        Return rows
    End Function

    Private Function FormatValue(v As Object) As String
        If v Is Nothing OrElse TypeOf v Is DBNull Then Return ""
        If TypeOf v Is DateTime Then
            Dim d = DirectCast(v, DateTime)
            Return If(d.TimeOfDay = TimeSpan.Zero, d.ToString("yyyy-MM-dd"), d.ToString("yyyy-MM-dd HH:mm:ss"))
        End If
        If TypeOf v Is Decimal Then Return DirectCast(v, Decimal).ToString(Globalization.CultureInfo.InvariantCulture)
        If TypeOf v Is Boolean Then Return If(DirectCast(v, Boolean), "1", "0")
        Return Convert.ToString(v, Globalization.CultureInfo.InvariantCulture)
    End Function

End Module
