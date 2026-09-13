Imports System.Data
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' The Dashboard's reorder forecast and its business advice — the maths and the
''' rules, checked against data set up to trigger each one. Every suggestion has
''' to be something an admin can act on, so each test states the situation it is
''' describing rather than just asserting a row count.
<TestClass>
Public Class InsightsTests

    <TestInitialize>
    Public Sub Setup()
        TestDb.Rebuild()
    End Sub

    Private Shared Function Titles(items As List(Of Insights.Recommendation)) As String
        Return String.Join(" | ", items.Select(Function(r) r.Kind & ": " & r.Title))
    End Function

    ''' Stock for a product, in the first warehouse.
    Private Shared Sub Stock(productId As Integer, qty As Integer)
        TestDb.Exec("INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) " &
                    "VALUES (@p, (SELECT MIN(WarehouseID) FROM Warehouses), 'TEST', @q)",
                    TestDb.P("@p", productId, "@q", qty))
    End Sub

    ' ===== reorder forecast =====

    <TestMethod>
    Public Sub Days_of_stock_is_how_long_the_shelf_lasts_at_the_selling_rate()
        Assert.AreEqual(14, Insights.DaysOfStock(onHand:=40, weeklyRate:=20D), "40 in stock selling 20 a week is two weeks")
        Assert.AreEqual(7, Insights.DaysOfStock(onHand:=10, weeklyRate:=10D))
    End Sub

    <TestMethod>
    Public Sub A_product_nobody_is_buying_has_no_days_of_stock_to_report()
        Assert.AreEqual(-1, Insights.DaysOfStock(onHand:=500, weeklyRate:=0D),
                        "with nothing selling there is no rate to divide by — days left is meaningless, not infinite")
    End Sub

    <TestMethod>
    Public Sub The_forecast_lists_each_active_product_with_what_is_on_the_shelf()
        Dim product = TestDb.AddProduct("Forecast Feed", 500D)
        Stock(product, 60)

        Dim forecast = Insights.ReorderForecast()
        Dim row = forecast.AsEnumerable().FirstOrDefault(Function(r) Convert.ToString(r("Product")) = "Forecast Feed")
        Assert.IsNotNull(row, "every active product belongs in the reorder table, selling or not")
        Assert.AreEqual(60, Convert.ToInt32(row("InStock")), "the table has to show what's actually on the shelf")
    End Sub

    ' ===== recommendations =====

    <TestMethod>
    Public Sub Stock_about_to_run_out_is_the_most_urgent_thing_on_the_list()
        Dim customer = TestDb.AddCustomer("Steady Buyer")
        Dim product = TestDb.AddProduct("Fast Mover", 500D)
        ' Six weeks of steady sales, against a shelf that won't last the 4-week
        ' lead time — exactly the case an admin needs warning about.
        For week = 1 To 6
            TestDb.AddInvoice(customer, product, Date.Today.AddDays(-7 * week), total:=20000D)
        Next
        Stock(product, 1)

        Dim advice = Insights.Recommendations()
        Dim urgent = advice.Where(Function(r) r.Kind = "Urgent").ToList()
        Assert.IsTrue(urgent.Any(Function(r) r.Title.Contains("Fast Mover")),
                      "a product running out before a delivery could arrive must be flagged: " & Titles(advice))
        Assert.AreEqual("Urgent", advice.First().Kind, "the most pressing advice belongs at the top: " & Titles(advice))
    End Sub

    <TestMethod>
    Public Sub Money_past_its_due_date_is_flagged_with_the_amount_owed()
        Dim customer = TestDb.AddCustomer("Slow Payer")
        Dim product = TestDb.AddProduct("Credit Feed", 500D)
        Dim invoice = TestDb.AddInvoice(customer, product, Date.Today.AddDays(-40), total:=50000D, paid:=0D, status:="Unpaid")
        TestDb.Exec("UPDATE Invoices SET DueDate = @d WHERE InvoiceID = @i",
                    TestDb.P("@d", Date.Today.AddDays(-10), "@i", invoice))

        Dim advice = Insights.Recommendations()
        Assert.IsTrue(advice.Any(Function(r) r.Kind = "Urgent" AndAlso r.Title.IndexOf("overdue", StringComparison.OrdinalIgnoreCase) >= 0),
                      "an invoice past its due date is money to chase: " & Titles(advice))
    End Sub

    <TestMethod>
    Public Sub A_paid_invoice_is_never_chased_as_overdue()
        Dim customer = TestDb.AddCustomer("Prompt Payer")
        Dim product = TestDb.AddProduct("Paid Feed", 500D)
        Dim invoice = TestDb.AddInvoice(customer, product, Date.Today.AddDays(-40), total:=50000D)
        TestDb.Exec("UPDATE Invoices SET DueDate = @d WHERE InvoiceID = @i",
                    TestDb.P("@d", Date.Today.AddDays(-10), "@i", invoice))

        Dim advice = Insights.Recommendations()
        Assert.IsFalse(advice.Any(Function(r) r.Title.IndexOf("overdue", StringComparison.OrdinalIgnoreCase) >= 0),
                       "it's settled — chasing the customer for it would be wrong: " & Titles(advice))
    End Sub

    <TestMethod>
    Public Sub A_drop_in_sales_against_last_month_is_called_out()
        Dim customer = TestDb.AddCustomer("Fading Customer")
        Dim product = TestDb.AddProduct("Trend Feed", 500D)
        TestDb.AddInvoice(customer, product, Date.Today.AddDays(-45), total:=400000D)
        TestDb.AddInvoice(customer, product, Date.Today.AddDays(-5), total:=50000D)

        Dim advice = Insights.Recommendations()
        Assert.IsTrue(advice.Any(Function(r) r.Title.IndexOf("down", StringComparison.OrdinalIgnoreCase) >= 0),
                      "a big fall against the month before is worth knowing: " & Titles(advice))
    End Sub

    <TestMethod>
    Public Sub A_rise_in_sales_is_reported_as_good_news_not_a_warning()
        Dim customer = TestDb.AddCustomer("Growing Customer")
        Dim product = TestDb.AddProduct("Rising Feed", 500D)
        TestDb.AddInvoice(customer, product, Date.Today.AddDays(-45), total:=50000D)
        TestDb.AddInvoice(customer, product, Date.Today.AddDays(-5), total:=400000D)

        Dim advice = Insights.Recommendations()
        Dim rise = advice.FirstOrDefault(Function(r) r.Title.IndexOf(" up ", StringComparison.OrdinalIgnoreCase) >= 0)
        Assert.IsNotNull(rise, "growth should be surfaced too: " & Titles(advice))
        Assert.AreEqual("Good", rise.Kind, "a rise in sales is not something to warn about")
    End Sub

    <TestMethod>
    Public Sub Stock_that_has_not_sold_in_weeks_is_reported_as_cash_sitting_still()
        Dim product = TestDb.AddProduct("Shelf Sitter", 1000D)
        Stock(product, 40)

        Dim advice = Insights.Recommendations()
        Assert.IsTrue(advice.Any(Function(r) r.Title.IndexOf("haven't sold", StringComparison.OrdinalIgnoreCase) >= 0),
                      "stock with no sales behind it is tied-up cash: " & Titles(advice))
    End Sub

    <TestMethod>
    Public Sub A_regular_who_stopped_buying_is_surfaced_for_a_win_back_call()
        Dim customer = TestDb.AddCustomer("Lapsed Regular")
        Dim product = TestDb.AddProduct("Regular Feed", 500D)
        ' Bought twice, but not for months — a regular who has drifted off.
        TestDb.AddInvoice(customer, product, Date.Today.AddDays(-120), total:=30000D)
        TestDb.AddInvoice(customer, product, Date.Today.AddDays(-90), total:=30000D)

        Dim advice = Insights.Recommendations()
        Dim quiet = advice.FirstOrDefault(Function(r) r.Title.IndexOf("gone quiet", StringComparison.OrdinalIgnoreCase) >= 0)
        Assert.IsNotNull(quiet, "a regular who stopped coming is the cheapest sale to win back: " & Titles(advice))
        Assert.IsTrue(quiet.Detail.Contains("Lapsed Regular"), "name the customer so someone can actually ring them")
    End Sub

    <TestMethod>
    Public Sub A_one_time_buyer_is_not_treated_as_a_lapsed_regular()
        Dim customer = TestDb.AddCustomer("Walk-in Once")
        Dim product = TestDb.AddProduct("One Off Feed", 500D)
        TestDb.AddInvoice(customer, product, Date.Today.AddDays(-100), total:=5000D)

        Dim advice = Insights.Recommendations()
        Dim quiet = advice.FirstOrDefault(Function(r) r.Title.IndexOf("gone quiet", StringComparison.OrdinalIgnoreCase) >= 0)
        If quiet IsNot Nothing Then
            Assert.IsFalse(quiet.Detail.Contains("Walk-in Once"),
                           "someone who bought once was never a regular — calling them a lapsed customer is noise")
        End If
    End Sub

    <TestMethod>
    Public Sub Every_suggestion_says_what_it_saw_and_what_to_do_about_it()
        Dim customer = TestDb.AddCustomer("Advice Customer")
        Dim product = TestDb.AddProduct("Advice Feed", 500D)
        TestDb.AddInvoice(customer, product, Date.Today.AddDays(-3), total:=20000D)
        Stock(product, 5)

        For Each item In Insights.Recommendations()
            Assert.IsFalse(String.IsNullOrWhiteSpace(item.Title), "a suggestion with no headline tells nobody anything")
            Assert.IsFalse(String.IsNullOrWhiteSpace(item.Detail), "advice without the reasoning is a black box: " & item.Title)
            Assert.IsTrue({"Urgent", "Watch", "Good"}.Contains(item.Kind), "unknown urgency: " & item.Kind)
        Next
    End Sub

End Class
