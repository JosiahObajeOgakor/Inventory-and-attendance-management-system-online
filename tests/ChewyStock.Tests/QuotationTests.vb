Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Quotations must never move stock, touch a customer's balance, or write to
''' the ledger — only converting one into a real sale (Sales.Save, followed by
''' Quotations.ConvertToSale) may do any of that. Alongside that, any line
''' priced away from its standard tier price — on a quotation or a sale — must
''' be recorded to PriceOverrides so who changed it is never lost.
<TestClass>
Public Class QuotationTests

    Private _product As Integer
    Private _warehouse As Integer
    Private _user As Integer
    Private _customer As Integer

    <TestInitialize>
    Public Sub Setup()
        _product = TestDb.AddProduct("Quotation test feed", cost:=1000D)
        _warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")
        _user = TestDb.Count("SELECT MIN(UserID) FROM Users")
        _customer = TestDb.AddCustomer("Quotation test customer " & Guid.NewGuid().ToString("N").Substring(0, 6))
    End Sub

    Private Function QuoteOf(unitPrice As Decimal) As Quotations.QuoteRequest
        Dim req As New Quotations.QuoteRequest() With {.CustomerID = _customer, .PriceTier = "Retailer"}
        req.Lines.Add(New Quotations.QuoteLine() With {
            .ProductID = _product, .ProductName = "Quotation test feed", .Quantity = 10, .UnitPrice = unitPrice})
        Return req
    End Function

    <TestMethod>
    Public Sub Saving_a_quotation_never_touches_stock_or_the_customers_balance()
        Stock.RecordProduction(_product, _warehouse, 50, Date.Today, "PROD-Q1", Nothing, _user)
        Dim balanceBefore = Convert.ToDecimal(TestDb.Scalar("SELECT Balance FROM Customers WHERE CustomerID=@id", TestDb.P("@id", _customer)))
        Dim movementsBefore = TestDb.Count("SELECT COUNT(*) FROM StockMovements WHERE ProductID=@p", TestDb.P("@p", _product))

        Dim saved = Quotations.Save(QuoteOf(1000D), _user)

        Assert.IsTrue(saved.QuotationID > 0)
        Assert.AreEqual(50, Stock.QuantityInWarehouse(_product, _warehouse), "a quotation must never take stock off the shelf")
        Assert.AreEqual(movementsBefore, TestDb.Count("SELECT COUNT(*) FROM StockMovements WHERE ProductID=@p", TestDb.P("@p", _product)),
                        "a quotation must not write a stock movement")
        Assert.AreEqual(balanceBefore, Convert.ToDecimal(TestDb.Scalar("SELECT Balance FROM Customers WHERE CustomerID=@id", TestDb.P("@id", _customer))),
                        "a quotation must never change what the customer owes")
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM QuotationItems WHERE QuotationID=@id", TestDb.P("@id", saved.QuotationID)))
        Assert.AreEqual("Open", Convert.ToString(TestDb.Scalar("SELECT [Status] FROM Quotations WHERE QuotationID=@id", TestDb.P("@id", saved.QuotationID))))
    End Sub

    <TestMethod>
    Public Sub Converting_a_quotation_creates_a_real_sale_and_marks_it_converted()
        Stock.RecordProduction(_product, _warehouse, 50, Date.Today, "PROD-Q2", Nothing, _user)
        Dim quote = Quotations.Save(QuoteOf(1000D), _user)

        Dim saleReq As New Sales.SaleRequest() With {
            .CustomerID = _customer, .CustomerName = "Quotation test customer", .CustomerType = "Retailer",
            .WarehouseID = _warehouse, .PaymentMethod = "Cash", .PriceTier = "Retailer"}
        saleReq.Lines.Add(New Sales.SaleLine() With {
            .ProductID = _product, .ProductName = "Quotation test feed", .Quantity = 10, .UnitPrice = 1000D, .UnitCost = 1000D})
        Dim sale = Sales.Save(saleReq, _user)
        Quotations.ConvertToSale(quote.QuotationID, sale.InvoiceID)

        Assert.AreEqual(40, Stock.QuantityInWarehouse(_product, _warehouse), "converting must actually deduct stock now")
        Dim row = TestDb.Table("SELECT [Status], ConvertedInvoiceID FROM Quotations WHERE QuotationID=@id", TestDb.P("@id", quote.QuotationID)).Rows(0)
        Assert.AreEqual("Converted", Convert.ToString(row("Status")))
        Assert.AreEqual(sale.InvoiceID, Convert.ToInt32(row("ConvertedInvoiceID")))
    End Sub

    <TestMethod>
    Public Sub Deleting_a_quotation_removes_it_and_its_lines()
        Dim quote = Quotations.Save(QuoteOf(1000D), _user)
        Quotations.Delete(quote.QuotationID)

        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Quotations WHERE QuotationID=@id", TestDb.P("@id", quote.QuotationID)))
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM QuotationItems WHERE QuotationID=@id", TestDb.P("@id", quote.QuotationID)))
    End Sub

    ' ===== price override audit trail =====

    <TestMethod>
    Public Sub A_quotation_line_priced_away_from_the_tier_price_is_logged()
        Dim quote = Quotations.Save(QuoteOf(750D), _user)   ' standard tier price is 1000

        Dim rows = TestDb.Table("SELECT StandardPrice, OverridePrice, ChangedByName FROM PriceOverrides WHERE DocType='Quotation' AND DocNumber=@n",
                                 TestDb.P("@n", quote.QuotationNumber))
        Assert.AreEqual(1, rows.Rows.Count, "an overridden line must be logged exactly once")
        Assert.AreEqual(1000D, Convert.ToDecimal(rows.Rows(0)("StandardPrice")))
        Assert.AreEqual(750D, Convert.ToDecimal(rows.Rows(0)("OverridePrice")))
        Assert.IsFalse(String.IsNullOrWhiteSpace(Convert.ToString(rows.Rows(0)("ChangedByName"))), "who changed it must be recorded")
    End Sub

    <TestMethod>
    Public Sub A_quotation_line_at_the_standard_tier_price_is_not_logged()
        Dim quote = Quotations.Save(QuoteOf(1000D), _user)   ' exactly the standard tier price

        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM PriceOverrides WHERE DocType='Quotation' AND DocNumber=@n",
                                        TestDb.P("@n", quote.QuotationNumber)))
    End Sub

    <TestMethod>
    Public Sub A_sale_line_priced_away_from_the_tier_price_is_logged()
        Stock.RecordProduction(_product, _warehouse, 50, Date.Today, "PROD-Q3", Nothing, _user)
        Dim req As New Sales.SaleRequest() With {
            .CustomerID = _customer, .CustomerName = "Quotation test customer", .CustomerType = "Retailer",
            .WarehouseID = _warehouse, .PaymentMethod = "Cash", .PriceTier = "Retailer"}
        req.Lines.Add(New Sales.SaleLine() With {
            .ProductID = _product, .ProductName = "Quotation test feed", .Quantity = 5, .UnitPrice = 850D, .UnitCost = 1000D})

        Dim saved = Sales.Save(req, _user)

        Dim rows = TestDb.Table("SELECT StandardPrice, OverridePrice FROM PriceOverrides WHERE DocType='Invoice' AND DocNumber=@n",
                                 TestDb.P("@n", saved.InvoiceNumber))
        Assert.AreEqual(1, rows.Rows.Count)
        Assert.AreEqual(1000D, Convert.ToDecimal(rows.Rows(0)("StandardPrice")))
        Assert.AreEqual(850D, Convert.ToDecimal(rows.Rows(0)("OverridePrice")))
    End Sub

End Class
