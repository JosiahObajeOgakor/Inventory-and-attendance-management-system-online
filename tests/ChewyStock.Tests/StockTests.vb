Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Recording production: the quantity reaches the warehouse, the run is dated,
''' repeat runs top up the same batch, and a new batch stays separate.
<TestClass>
Public Class StockTests

    Private _product As Integer
    Private _warehouse As Integer
    Private _user As Integer

    <TestInitialize>
    Public Sub Setup()
        _product = TestDb.AddProduct("Production test feed")
        _warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")
        _user = TestDb.Count("SELECT MIN(UserID) FROM Users")
    End Sub

    <TestMethod>
    Public Sub Production_adds_to_stock_and_is_logged_under_its_date()
        Dim producedOn = Date.Today.AddDays(-9)
        Assert.AreEqual("", Stock.RecordProduction(_product, _warehouse, 40, producedOn, "PROD-A", Nothing, _user))

        Assert.AreEqual(40, Stock.QuantityInWarehouse(_product, _warehouse))
        Dim row = TestDb.Table(
            "SELECT MovementType, Quantity, ReferenceType, MovementDate, UserID FROM StockMovements WHERE ProductID=@p",
            TestDb.P("@p", _product)).Rows(0)
        Assert.AreEqual("IN", Convert.ToString(row("MovementType")).Trim())
        Assert.AreEqual(40, Convert.ToInt32(row("Quantity")))
        Assert.AreEqual("Production", Convert.ToString(row("ReferenceType")).Trim())
        Assert.AreEqual(producedOn, Convert.ToDateTime(row("MovementDate")).Date, "the production date must be kept")
        Assert.AreEqual(_user, Convert.ToInt32(row("UserID")), "who recorded it must be kept")
    End Sub

    <TestMethod>
    Public Sub Producing_into_the_same_batch_tops_it_up()
        Stock.RecordProduction(_product, _warehouse, 50, Date.Today.AddDays(-3), "PROD-SAME", Nothing, _user)
        Stock.RecordProduction(_product, _warehouse, 30, Date.Today, "PROD-SAME", Nothing, _user)

        Assert.AreEqual(80, Stock.QuantityInWarehouse(_product, _warehouse))
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM StockBatches WHERE ProductID=@p AND BatchNumber='PROD-SAME'", TestDb.P("@p", _product)),
                        "the same batch number must not be duplicated")
        Assert.AreEqual(2, TestDb.Count("SELECT COUNT(*) FROM StockMovements WHERE ProductID=@p", TestDb.P("@p", _product)),
                        "both runs must show in the history")
    End Sub

    <TestMethod>
    Public Sub A_new_batch_number_starts_a_separate_batch()
        Stock.RecordProduction(_product, _warehouse, 20, Date.Today, "PROD-B1", Nothing, _user)
        Stock.RecordProduction(_product, _warehouse, 25, Date.Today, "PROD-B2", Nothing, _user)

        Assert.AreEqual(45, Stock.QuantityInWarehouse(_product, _warehouse))
        Assert.AreEqual(2, TestDb.Count("SELECT COUNT(*) FROM StockBatches WHERE ProductID=@p", TestDb.P("@p", _product)))
    End Sub

    <TestMethod>
    Public Sub Blank_batch_number_is_named_after_the_production_date()
        Dim producedOn = New Date(2026, 8, 21)
        Stock.RecordProduction(_product, _warehouse, 10, producedOn, "   ", Nothing, _user)
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM StockBatches WHERE ProductID=@p AND BatchNumber='PROD-260821'", TestDb.P("@p", _product)))
    End Sub

    <TestMethod>
    Public Sub Expiry_date_is_stored_when_given()
        Dim expiry = Date.Today.AddMonths(8)
        Stock.RecordProduction(_product, _warehouse, 5, Date.Today, "PROD-EXP", expiry, _user)
        Dim stored = TestDb.Scalar("SELECT ExpiryDate FROM StockBatches WHERE ProductID=@p AND BatchNumber='PROD-EXP'", TestDb.P("@p", _product))
        Assert.AreEqual(expiry, Convert.ToDateTime(stored).Date)
    End Sub

    <TestMethod>
    Public Sub Zero_quantity_is_refused_and_changes_nothing()
        Dim err = Stock.RecordProduction(_product, _warehouse, 0, Date.Today, "PROD-ZERO", Nothing, _user)
        Assert.AreNotEqual("", err)
        Assert.AreEqual(0, Stock.QuantityInWarehouse(_product, _warehouse))
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM StockMovements WHERE ProductID=@p", TestDb.P("@p", _product)))
    End Sub

    ' ===== overselling =====

    Private Function SaleOf(qty As Integer) As Sales.SaleRequest
        Dim req As New Sales.SaleRequest() With {
            .CustomerID = TestDb.AddCustomer("Oversell test " & Guid.NewGuid().ToString("N").Substring(0, 6)),
            .CustomerName = "Oversell test",
            .CustomerType = "Retailer",
            .SaleDate = Date.Today,
            .WarehouseID = _warehouse,
            .PaymentMethod = "Cash"}
        req.Lines.Add(New Sales.SaleLine() With {
            .ProductID = _product, .ProductName = "Production test feed",
            .Quantity = qty, .UnitPrice = 500D, .UnitCost = 100D})
        Return req
    End Function

    <TestMethod>
    Public Sub A_sale_for_more_than_is_in_stock_is_refused()
        Stock.RecordProduction(_product, _warehouse, 100, Date.Today, "PROD-OS", Nothing, _user)

        Assert.ThrowsException(Of Sales.InsufficientStockException)(
            Sub() Sales.Save(SaleOf(10000), _user),
            "selling 10,000 against 100 on hand must be refused")
    End Sub

    <TestMethod>
    Public Sub A_refused_sale_leaves_no_invoice_and_no_stock_movement()
        Stock.RecordProduction(_product, _warehouse, 100, Date.Today, "PROD-OS", Nothing, _user)
        Dim invoicesBefore = TestDb.Count("SELECT COUNT(*) FROM Invoices")

        Try
            Sales.Save(SaleOf(10000), _user)
        Catch ex As Sales.InsufficientStockException
        End Try

        Assert.AreEqual(100, Stock.QuantityInWarehouse(_product, _warehouse), "stock must be untouched")
        Assert.AreEqual(invoicesBefore, TestDb.Count("SELECT COUNT(*) FROM Invoices"), "no invoice may be left behind")
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM StockMovements WHERE ProductID=@p", TestDb.P("@p", _product)),
                        "only the production IN may remain")
    End Sub

    <TestMethod>
    Public Sub The_shortfall_says_what_is_missing_and_what_to_restock()
        Stock.RecordProduction(_product, _warehouse, 100, Date.Today, "PROD-OS", Nothing, _user)

        Dim shortfalls = Sales.FindShortfalls(SaleOf(10000))
        Assert.AreEqual(1, shortfalls.Count)
        Assert.AreEqual(10000, shortfalls(0).Requested)
        Assert.AreEqual(100, shortfalls(0).Available)
        Assert.AreEqual(9900, shortfalls(0).ShortBy)
        Assert.IsNotNull(shortfalls(0).Advice, "a refused line must carry restocking advice")
    End Sub

    <TestMethod>
    Public Sub A_sale_within_stock_still_goes_through_and_deducts()
        Stock.RecordProduction(_product, _warehouse, 100, Date.Today, "PROD-OK", Nothing, _user)

        Dim saved = Sales.Save(SaleOf(40), _user)

        Assert.IsTrue(saved.InvoiceID > 0)
        Assert.AreEqual(60, Stock.QuantityInWarehouse(_product, _warehouse), "the sold units must come off the shelf")
    End Sub

    <TestMethod>
    Public Sub A_sale_spanning_several_batches_is_allowed_and_drains_them_in_order()
        ' Neither batch covers 90 on its own — together they do.
        Stock.RecordProduction(_product, _warehouse, 60, Date.Today, "PROD-B1", Date.Today.AddMonths(1), _user)
        Stock.RecordProduction(_product, _warehouse, 50, Date.Today, "PROD-B2", Date.Today.AddMonths(9), _user)

        Sales.Save(SaleOf(90), _user)

        Assert.AreEqual(20, Stock.QuantityInWarehouse(_product, _warehouse))
        Assert.AreEqual(0, TestDb.Count("SELECT QuantityOnHand FROM StockBatches WHERE ProductID=@p AND BatchNumber='PROD-B1'", TestDb.P("@p", _product)),
                        "the batch expiring first must be emptied first")
    End Sub

    <TestMethod>
    Public Sub Stock_can_never_be_driven_negative_by_a_sale()
        Stock.RecordProduction(_product, _warehouse, 100, Date.Today, "PROD-NEG", Nothing, _user)

        Try
            Sales.Save(SaleOf(101), _user)
        Catch ex As Sales.InsufficientStockException
        End Try

        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM StockBatches WHERE ProductID=@p AND QuantityOnHand < 0", TestDb.P("@p", _product)))
    End Sub

End Class
