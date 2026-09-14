Imports System.Data
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' The algorithms as they reach the two screens people actually work on: the
''' till and the purchase order.
'''
''' These run against generated history rather than a handful of hand-written
''' rows, because that's the honest test — a forecast or a basket rule needs
''' weeks of data before it says anything at all, and a test that fakes that
''' proves nothing about the real screen.
<TestClass>
Public Class AdviceTests

    Private Shared _userId As Integer

    ''' The history is built once and then reused, not rebuilt per test.
    ''' Generating it is real work — dozens of sales and purchase orders — and
    ''' every test here only reads it or sets the stock level it wants.
    ''' Rebuilding per test turned this class into an hour and a half of
    ''' waiting, which is the same as not having the tests at all.
    '''
    ''' Its presence is checked rather than assumed: the database is shared with
    ''' every other test class, and some of them rebuild it from scratch. MSTest
    ''' gives no ordering between classes, so the fixture can vanish between two
    ''' of our own tests — this puts it back when it has.
    <TestInitialize>
    Public Sub EnsureHistory()
        If SampleData.SampleInvoiceCount() > 0 Then Return
        TestDb.Rebuild()
        _userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
        SampleData.Generate(_userId, weeks:=12)
    End Sub

    Private Function AnchorProduct() As Integer
        Return TestDb.Count("SELECT TOP 1 ProductID FROM Products WHERE IsActive = 1 ORDER BY ProductID")
    End Function

    ' ===== the till =====

    <TestMethod>
    Public Sub The_till_suggests_what_usually_goes_with_the_product_just_added()

        Dim advice = SaleAdvice.Upsell({AnchorProduct()})

        Assert.IsNotNull(advice, "the planted pairing should be found — that's what it was planted for")
        Assert.IsTrue(advice.IndexOf("usually take", StringComparison.OrdinalIgnoreCase) >= 0,
                      "and it has to read as a prompt a clerk can say out loud: " & advice)
    End Sub

    <TestMethod>
    Public Sub Nothing_is_suggested_for_a_product_already_on_the_sale()
        Dim everything = TestDb.Table("SELECT ProductID FROM Products WHERE IsActive = 1").
            AsEnumerable().Select(Function(r) Convert.ToInt32(r("ProductID"))).ToList()

        Assert.IsNull(SaleAdvice.Upsell(everything),
                      "suggesting something already in the basket is noise")
    End Sub

    <TestMethod>
    Public Sub An_empty_sale_gets_no_suggestion()
        Assert.IsNull(SaleAdvice.Upsell(New List(Of Integer)))
    End Sub

    <TestMethod>
    Public Sub A_wildly_oversized_quantity_is_queried_before_it_deducts_stock()
        Dim product = AnchorProduct()

        Dim warning = SaleAdvice.QuantityLooksUnusual(product, 5000)

        Assert.IsNotNull(warning, "5,000 against a history of single digits has to be questioned")
        Assert.IsTrue(warning.IndexOf("Check the quantity", StringComparison.OrdinalIgnoreCase) >= 0, warning)
    End Sub

    <TestMethod>
    Public Sub An_ordinary_quantity_is_not_questioned()

        Assert.IsNull(SaleAdvice.QuantityLooksUnusual(AnchorProduct(), 3),
                      "a warning on a normal sale trains people to click through warnings")
    End Sub

    <TestMethod>
    Public Sub A_smaller_than_usual_quantity_is_never_questioned()
        Assert.IsNull(SaleAdvice.QuantityLooksUnusual(AnchorProduct(), 1),
                      "selling fewer than usual is not a mistake")
    End Sub

    <TestMethod>
    Public Sub A_product_with_no_history_is_never_questioned()
        Dim fresh = TestDb.AddProduct("Brand New Feed")
        Assert.IsNull(SaleAdvice.QuantityLooksUnusual(fresh, 900),
                      "with nothing to compare against, a warning would be a guess")
    End Sub

    <TestMethod>
    Public Sub Selling_the_shelf_down_to_nothing_warns_about_the_lead_time()
        Dim product = AnchorProduct()
        Dim onHand = TestDb.Count("SELECT ISNULL(SUM(QuantityOnHand),0) FROM StockBatches WHERE ProductID=@p",
                                  TestDb.P("@p", product))

        Dim warning = SaleAdvice.StockoutRiskAfterSale(product, onHand)

        Assert.IsNotNull(warning, "clearing the shelf should say so")
        Assert.IsTrue(warning.IndexOf("reorder", StringComparison.OrdinalIgnoreCase) >= 0, warning)
    End Sub

    <TestMethod>
    Public Sub A_well_stocked_product_raises_no_stockout_warning()
        TestDb.Exec("UPDATE StockBatches SET QuantityOnHand = 400")
        Assert.IsNull(SaleAdvice.StockoutRiskAfterSale(AnchorProduct(), 1),
                      "hundreds in stock and one sold is not a stockout risk")
    End Sub

    <TestMethod>
    Public Sub A_customer_is_placed_in_a_segment_once_there_are_enough_of_them()
        For i = 1 To 6
            TestDb.AddCustomer("Segment Customer " & i)
        Next
        Dim customerId = TestDb.Count("SELECT TOP 1 CustomerID FROM Customers ORDER BY CustomerID")

        Dim segment = SaleAdvice.CustomerSegment(customerId)
        Assert.IsNotNull(segment)
        Assert.IsTrue(segment.Contains("customer"), "the label has to mean something to a clerk: " & segment)
    End Sub

    ' ===== the purchase order =====

    <TestMethod>
    Public Sub The_order_screen_suggests_what_to_buy_with_quantities_and_a_deadline()
        ' Draw the shelves down so there is genuinely something to reorder.
        TestDb.Exec("UPDATE StockBatches SET QuantityOnHand = 2")
        Dim supplierId = TestDb.Count("SELECT TOP 1 SupplierID FROM Suppliers ORDER BY SupplierID")

        Dim suggestions = PurchaseAdvice.SuggestOrder(supplierId)

        Assert.IsTrue(suggestions.Count > 0, "with empty shelves and steady sales there is plenty to order")
        Dim first = suggestions.First()
        Assert.IsTrue(first.Quantity > 0, "a suggestion without a quantity is not a suggestion")
        Assert.IsTrue(first.RunsOutOn.HasValue, "a deadline is what turns a quantity into an order")
        Assert.IsTrue(first.Basis.Contains("week"), "and it has to show its working: " & first.Basis)
    End Sub

    <TestMethod>
    Public Sub The_most_urgent_line_is_suggested_first()
        TestDb.Exec("UPDATE StockBatches SET QuantityOnHand = 2")
        Dim supplierId = TestDb.Count("SELECT TOP 1 SupplierID FROM Suppliers ORDER BY SupplierID")

        Dim suggestions = PurchaseAdvice.SuggestOrder(supplierId)
        For i = 1 To suggestions.Count - 1
            Dim earlier = If(suggestions(i - 1).RunsOutOn.HasValue, suggestions(i - 1).RunsOutOn.Value, Date.MaxValue)
            Dim later = If(suggestions(i).RunsOutOn.HasValue, suggestions(i).RunsOutOn.Value, Date.MaxValue)
            Assert.IsTrue(earlier <= later, "whatever runs out soonest has to be read first")
        Next
    End Sub

    <TestMethod>
    Public Sub A_well_stocked_supplier_needs_no_order()
        TestDb.Exec("UPDATE StockBatches SET QuantityOnHand = 100000")
        Dim supplierId = TestDb.Count("SELECT TOP 1 SupplierID FROM Suppliers ORDER BY SupplierID")

        Assert.AreEqual(0, PurchaseAdvice.SuggestOrder(supplierId).Count,
                        "suggesting an order for a full warehouse ties up cash for nothing")
    End Sub

    <TestMethod>
    Public Sub A_cost_far_off_the_suppliers_usual_is_queried()
        Dim supplierId = TestDb.Count("SELECT TOP 1 SupplierID FROM Suppliers ORDER BY SupplierID")
        Dim productId = TestDb.Count(
            "SELECT TOP 1 poi.ProductID FROM PurchaseOrderItems poi JOIN PurchaseOrders po ON po.POID = poi.POID WHERE po.SupplierID=@s",
            TestDb.P("@s", supplierId))

        Dim warning = PurchaseAdvice.CostLooksUnusual(supplierId, productId, 900000D)

        Assert.IsNotNull(warning, "a cost orders of magnitude off has to be caught before the order goes out")
        Assert.IsTrue(warning.IndexOf("usual", StringComparison.OrdinalIgnoreCase) >= 0, warning)
    End Sub

    <TestMethod>
    Public Sub An_ordinary_cost_passes_without_comment()
        Dim supplierId = TestDb.Count("SELECT TOP 1 SupplierID FROM Suppliers ORDER BY SupplierID")
        Dim row = TestDb.Table(
            "SELECT TOP 1 poi.ProductID, poi.UnitCost FROM PurchaseOrderItems poi " &
            "JOIN PurchaseOrders po ON po.POID = poi.POID WHERE po.SupplierID=@s", TestDb.P("@s", supplierId)).Rows(0)

        Assert.IsNull(PurchaseAdvice.CostLooksUnusual(supplierId, Convert.ToInt32(row("ProductID")),
                                                      Convert.ToDecimal(row("UnitCost"))),
                      "the supplier's own usual price must never be flagged")
    End Sub

    <TestMethod>
    Public Sub A_supplier_with_no_cost_history_is_never_queried()
        Dim supplierId = DataAccess.ExecuteScalarInsert(
            "INSERT INTO Suppliers (Name, Category) VALUES ('Brand New Supplier', 'Feed')", Nothing)
        Assert.IsNull(PurchaseAdvice.CostLooksUnusual(supplierId, AnchorProduct(), 999999D),
                      "with no history there is no 'usual' to be unusual against")
    End Sub

    <TestMethod>
    Public Sub Ordering_half_of_a_pair_that_sells_together_is_pointed_out()

        Dim advice = PurchaseAdvice.MissingPartner({AnchorProduct()})

        Assert.IsNotNull(advice, "arriving with one half of a pair means the pair can't be sold")
        Assert.IsTrue(advice.IndexOf("sell together", StringComparison.OrdinalIgnoreCase) >= 0, advice)
    End Sub

End Class
