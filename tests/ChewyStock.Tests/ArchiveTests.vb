Imports System.IO
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Database storage: what the archive would remove, what it must keep, that the
''' files are written before anything is deleted, and that a failed export
''' leaves the database untouched.
<TestClass>
Public Class ArchiveTests

    Private Shared ReadOnly Cutoff As Date = New Date(Date.Today.Year, 1, 1)
    Private _customer As Integer
    Private _product As Integer
    Private _folder As String

    <TestInitialize>
    Public Sub Setup()
        TestDb.Rebuild()
        _customer = TestDb.AddCustomer("Archive Customer")
        _product = TestDb.AddProduct("Archive Feed")
        _folder = Path.Combine(Path.GetTempPath(), "chewystock_archive_" & Guid.NewGuid().ToString("N").Substring(0, 8))
    End Sub

    <TestCleanup>
    Public Sub Cleanup()
        Try
            If Directory.Exists(_folder) Then Directory.Delete(_folder, True)
        Catch
        End Try
    End Sub

    Private Function OldPaidInvoice() As Integer
        Return TestDb.AddInvoice(_customer, _product, Cutoff.AddMonths(-6), 12000D)
    End Function

    <TestMethod>
    Public Sub Preview_counts_old_settled_records_only()
        OldPaidInvoice()
        TestDb.AddInvoice(_customer, _product, Cutoff.AddMonths(-5), 9000D, paid:=0D, status:="Unpaid")   ' unpaid: keep
        TestDb.AddInvoice(_customer, _product, Date.Today, 7000D)                                        ' recent: keep

        Dim preview = DbMaintenance.Preview(Cutoff)
        Dim invoices = preview.First(Function(p) p.Table = "Invoices").Rows
        Assert.AreEqual(1, invoices, "only the old, fully paid invoice should be archivable")
    End Sub

    <TestMethod>
    Public Sub Archive_writes_the_files_then_removes_the_records()
        Dim archived = OldPaidInvoice()
        Dim keptRecent = TestDb.AddInvoice(_customer, _product, Date.Today, 7000D)
        Dim keptUnpaid = TestDb.AddInvoice(_customer, _product, Cutoff.AddMonths(-2), 5000D, paid:=0D, status:="Unpaid")
        TestDb.Exec("INSERT INTO Expenses (Category, ExpenseDate, Amount, Note, CreatedByUserID) VALUES ('Rent', @d, 4000, 'old rent', (SELECT MIN(UserID) FROM Users))",
                    TestDb.P("@d", Cutoff.AddMonths(-3)))

        Dim moved = DbMaintenance.Archive(Cutoff, _folder)

        Assert.IsTrue(moved >= 3, "invoice, its line and the old expense should all be archived")
        Assert.IsTrue(File.Exists(Path.Combine(_folder, "Archive.xlsx")), "the Excel workbook must be written")
        Assert.IsTrue(File.Exists(Path.Combine(_folder, "csv", "Invoices.csv")), "a CSV per table must be written")
        Assert.IsTrue(File.ReadAllText(Path.Combine(_folder, "csv", "Invoices.csv")).Length > 50, "the CSV must contain the archived rows")

        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", archived)))
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", keptRecent)), "recent sales stay")
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", keptUnpaid)), "unpaid sales stay whatever their age")
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM InvoiceItems WHERE InvoiceID=@i", TestDb.P("@i", archived)), "the invoice lines go too")
    End Sub

    <TestMethod>
    Public Sub Unredeemed_rebates_are_kept_and_note_the_archived_invoice()
        Dim invoice = OldPaidInvoice()
        TestDb.Exec("INSERT INTO RebateEntries (CustomerID, InvoiceID, EntryDate, Amount, [Status]) VALUES (@c, @i, @d, 120, 'Accrued')",
                    TestDb.P("@c", _customer, "@i", invoice, "@d", Cutoff.AddMonths(-6)))

        DbMaintenance.Archive(Cutoff, _folder)

        Dim rebate = TestDb.Table("SELECT InvoiceID, Note FROM RebateEntries WHERE CustomerID=@c", TestDb.P("@c", _customer))
        Assert.AreEqual(1, rebate.Rows.Count, "an unredeemed rebate is money owed — it must survive archiving")
        Assert.IsTrue(IsDBNull(rebate.Rows(0)("InvoiceID")), "its link to the archived invoice is cleared")
        Assert.IsTrue(Convert.ToString(rebate.Rows(0)("Note")).Contains("archived"), "the note should say which invoice was archived")
    End Sub

    <TestMethod>
    Public Sub Nothing_is_deleted_when_the_files_cannot_be_written()
        OldPaidInvoice()
        Dim before = TestDb.Count("SELECT COUNT(*) FROM Invoices")

        ' A folder where Archive.xlsx should go makes the export fail part-way.
        Directory.CreateDirectory(Path.Combine(_folder, "Archive.xlsx"))
        Try
            DbMaintenance.Archive(Cutoff, _folder)
            Assert.Fail("the archive should have failed")
        Catch ex As Exception
            ' expected
        End Try

        Assert.AreEqual(before, TestDb.Count("SELECT COUNT(*) FROM Invoices"),
                        "a failed export must roll back — the records stay in the database")
    End Sub

    <TestMethod>
    Public Sub Usage_reports_the_ten_gigabyte_localdb_limit()
        Dim usage = DbMaintenance.GetUsage()
        Assert.AreEqual(10240.0, usage.LimitMB, "LocalDB caps a database at 10 GB")
        Assert.IsTrue(usage.UsedMB > 0 AndAlso usage.UsedMB < usage.LimitMB)
        Assert.IsTrue(usage.Percent > 0 AndAlso usage.Percent < 100)
    End Sub

End Class
