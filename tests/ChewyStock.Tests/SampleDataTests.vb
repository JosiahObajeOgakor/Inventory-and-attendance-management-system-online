Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' The sample-history generator itself: that it writes, that it repeats exactly,
''' and — the two that matter most — that it refuses to touch a database with
''' real trading on it, and that everything it wrote can be taken back out
''' without disturbing anything real.
'''
''' These each need their own clean database, so they're kept apart from the
''' tests that merely read generated history.
<TestClass>
Public Class SampleDataTests

    Private _userId As Integer

    <TestInitialize>
    Public Sub Setup()
        TestDb.Rebuild()
        _userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
    End Sub

    <TestMethod>
    Public Sub Generated_history_is_written_and_can_be_taken_away_again()
        Dim written = SampleData.Generate(_userId, weeks:=10)

        Assert.IsTrue(written > 20, "ten weeks should produce a few dozen sales, got " & written)
        Assert.AreEqual(written, SampleData.SampleInvoiceCount())

        SampleData.Remove()
        Assert.AreEqual(0, SampleData.SampleInvoiceCount(), "sample data that can't be removed is a trap")
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Invoices"), "and it must not leave real-looking rows behind")
    End Sub

    <TestMethod>
    Public Sub Generating_twice_gives_the_identical_history()
        Dim first = SampleData.Generate(_userId, weeks:=6)
        Dim firstTotal = Convert.ToDecimal(TestDb.Scalar("SELECT SUM(TotalAmount) FROM Invoices"))
        SampleData.Remove()

        Dim second = SampleData.Generate(_userId, weeks:=6)
        Assert.AreEqual(first, second, "same seed, same number of sales")
        Assert.AreEqual(firstTotal, Convert.ToDecimal(TestDb.Scalar("SELECT SUM(TotalAmount) FROM Invoices")),
                        "and the same money — a demo that shifts between runs is worthless")
    End Sub

    <TestMethod>
    Public Sub It_refuses_to_scribble_on_a_database_with_real_sales()
        Dim customer = TestDb.AddCustomer("Real Customer")
        Dim product = TestDb.AddProduct("Real Feed")
        TestDb.AddInvoice(customer, product, Date.Today)

        Assert.ThrowsException(Of InvalidOperationException)(Sub() SampleData.Generate(_userId),
            "mixing invented sales into real books would corrupt every report")
    End Sub

    <TestMethod>
    Public Sub Removing_sample_data_leaves_real_sales_alone()
        SampleData.Generate(_userId, weeks:=4)
        ' A genuine sale arrives afterwards.
        Dim customer = TestDb.AddCustomer("Genuine Customer")
        Dim product = TestDb.AddProduct("Genuine Feed")
        TestDb.AddInvoice(customer, product, Date.Today)

        SampleData.Remove()

        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Invoices"),
                        "the real sale must survive the clear-out")
    End Sub

End Class
