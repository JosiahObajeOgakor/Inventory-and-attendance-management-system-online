Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' A customer who already owes from an earlier sale must never have that old
''' debt quietly dropped by a new one: it's read fresh at save time, today's
''' sale is settled first out of whatever's paid, anything paid beyond that
''' chips away at the old debt (oldest invoice first), and whatever's still
''' unpaid — old, new, or both — stays visible on the customer's balance.
<TestClass>
Public Class CustomerDebtTests

    Private _customer As Integer
    Private _product As Integer
    Private _warehouse As Integer
    Private _user As Integer

    <TestInitialize>
    Public Sub Setup()
        _customer = TestDb.AddCustomer("Debt Customer " & Guid.NewGuid().ToString("N").Substring(0, 5))
        _product = TestDb.AddProduct("Debt Feed " & Guid.NewGuid().ToString("N").Substring(0, 5), 600D)
        _warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")
        _user = TestDb.Count("SELECT MIN(UserID) FROM Users")
        TestDb.Exec("INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p, @w, 'DEBT', 1000)",
                    TestDb.P("@p", _product, "@w", _warehouse))
    End Sub

    Private Function Request(qty As Integer, paid As Decimal) As Sales.SaleRequest
        Dim req As New Sales.SaleRequest() With {
            .CustomerID = _customer, .CustomerName = "Debt Customer", .CustomerType = "Retailer",
            .SaleDate = Date.Today, .PriceTier = "Retailer", .WarehouseID = _warehouse,
            .PaymentMethod = "Cash", .PaidNow = paid}
        req.Lines.Add(New Sales.SaleLine() With {.ProductID = _product, .ProductName = "Debt Feed", .Quantity = qty, .UnitPrice = 1000D, .UnitCost = 600D})
        Return req
    End Function

    Private Function CustomerBalance() As Decimal
        Return Convert.ToDecimal(TestDb.Scalar("SELECT Balance FROM Customers WHERE CustomerID=@c", TestDb.P("@c", _customer)))
    End Function

    ' ===== a new sale sees and reports the old debt =====

    <TestMethod>
    Public Sub A_new_sale_reports_what_the_customer_already_owed()
        Sales.Save(Request(qty:=2, paid:=0D), _user)   ' first sale, unpaid — 2000 owed

        Dim second = Sales.Save(Request(qty:=1, paid:=1000D), _user)   ' second sale, paid in full

        Assert.AreEqual(2000D, second.PreviousBalance, "the old debt must be read, not ignored")
        Assert.AreEqual("Paid", second.Status, "today's own sale is what was paid for")
        Assert.AreEqual(2000D, second.RemainingBalance, "the old debt is still owed — it can't just vanish")
        Assert.AreEqual(2000D, CustomerBalance(), "nothing was paid toward the old debt, so it's unchanged")
    End Sub

    <TestMethod>
    Public Sub Paying_more_than_todays_sale_clears_old_debt_first_come_first_served()
        Sales.Save(Request(qty:=2, paid:=0D), _user)   ' 2000 owed from before

        ' Today's sale is 1000; hand over 2500 — 1000 for today, 1500 off the old debt.
        Dim second = Sales.Save(Request(qty:=1, paid:=2500D), _user)

        Assert.AreEqual("Paid", second.Status, "today's sale is settled first")
        Assert.AreEqual(1500D, second.AppliedToPreviousBalance)
        Assert.AreEqual(500D, second.RemainingBalance, "2000 owed − 1500 paid toward it = 500 left")
        Assert.AreEqual(500D, CustomerBalance())
    End Sub

    <TestMethod>
    Public Sub Paying_enough_to_cover_everything_clears_the_whole_account()
        Sales.Save(Request(qty:=2, paid:=0D), _user)   ' 2000 owed

        Dim second = Sales.Save(Request(qty:=1, paid:=3000D), _user)   ' today's 1000 + the old 2000

        Assert.AreEqual(0D, second.RemainingBalance)
        Assert.AreEqual(0D, CustomerBalance())
        Assert.AreEqual("Paid", second.Status)
    End Sub

    <TestMethod>
    Public Sub Overpaying_beyond_everything_owed_is_capped_not_lost_as_credit()
        Sales.Save(Request(qty:=2, paid:=0D), _user)   ' 2000 owed

        ' Hands over far more than is owed in total (1000 today + 2000 old = 3000).
        Dim second = Sales.Save(Request(qty:=1, paid:=10000D), _user)

        Assert.AreEqual(0D, second.RemainingBalance)
        Assert.AreEqual(0D, CustomerBalance(), "the customer can't end up with a negative balance from an accidental overpay")
    End Sub

    <TestMethod>
    Public Sub Not_covering_even_todays_sale_leaves_the_old_debt_completely_untouched()
        Sales.Save(Request(qty:=2, paid:=0D), _user)   ' 2000 owed

        Dim second = Sales.Save(Request(qty:=1, paid:=400D), _user)   ' today's own 1000 not even covered

        Assert.AreEqual("Partial", second.Status)
        Assert.AreEqual(0D, second.AppliedToPreviousBalance, "nothing left over to touch the old debt")
        Assert.AreEqual(2600D, second.RemainingBalance, "2000 old + 600 left on today's own sale")
        Assert.AreEqual(2600D, CustomerBalance())
    End Sub

    ' ===== the paper trail =====

    <TestMethod>
    Public Sub Extra_payment_toward_old_debt_is_recorded_on_the_ledger_and_the_old_invoice()
        Dim first = Sales.Save(Request(qty:=2, paid:=0D), _user)   ' 2000 owed, Unpaid

        Dim second = Sales.Save(Request(qty:=1, paid:=3000D), _user)   ' today's 1000 + the old 2000, fully clearing it

        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Ledger WHERE Reference = @r AND EntryType = 'Credit'",
                                        TestDb.P("@r", second.InvoiceNumber)),
                        "the credit toward old debt must be traceable back to the sale that generated it")
        Assert.AreEqual("Paid", Convert.ToString(TestDb.Scalar("SELECT [Status] FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", first.InvoiceID))),
                        "the OLD invoice itself is marked paid once its own total is covered")
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Payments WHERE InvoiceID=@i", TestDb.P("@i", first.InvoiceID)),
                        "a Payments row against the old invoice, not just a balance-only credit")
    End Sub

    <TestMethod>
    Public Sub Two_old_invoices_are_paid_off_oldest_first()
        Dim first = Sales.Save(Request(qty:=1, paid:=0D), _user)    ' 1000 owed, oldest
        Dim secondOld = Sales.Save(Request(qty:=1, paid:=0D), _user) ' another 1000 owed

        ' Today's sale (1000) + 1500 extra — enough to fully clear the oldest and
        ' half of the second, none left for the newest sale to spare further.
        Dim third = Sales.Save(Request(qty:=1, paid:=2500D), _user)

        Assert.AreEqual("Paid", Convert.ToString(TestDb.Scalar("SELECT [Status] FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", first.InvoiceID))),
                        "the oldest debt is cleared first")
        Assert.AreEqual("Partial", Convert.ToString(TestDb.Scalar("SELECT [Status] FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", secondOld.InvoiceID))))
        Assert.AreEqual(500D, Convert.ToDecimal(TestDb.Scalar("SELECT AmountPaid FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", secondOld.InvoiceID))))
        Assert.AreEqual(500D, CustomerBalance(), "1000+1000 owed − 1000(today) − 1500(extra) = 500 left")
    End Sub

    ' ===== "Record payment" on the Customers screen follows the same rule =====

    <TestMethod>
    Public Sub Record_payment_spreads_across_outstanding_invoices_oldest_first_without_overpaying_one()
        Dim first = Sales.Save(Request(qty:=1, paid:=0D), _user)     ' 1000 owed
        Dim secondInv = Sales.Save(Request(qty:=2, paid:=0D), _user) ' 2000 owed — Balance now 3000

        DataAccess.InTransaction(
            Function(conn, tx)
                Sales.ApplyPaymentToOutstandingInvoices(conn, tx, _customer, 1500D, Date.Today, "Cash", _user)
                DataAccess.Exec(conn, tx, "UPDATE Customers SET Balance = Balance - @a WHERE CustomerID=@c", TestDb.P("@a", 1500D, "@c", _customer))
                Return True
            End Function)

        Assert.AreEqual("Paid", Convert.ToString(TestDb.Scalar("SELECT [Status] FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", first.InvoiceID))),
                        "the older, smaller invoice is fully cleared first")
        Assert.AreEqual(500D, Convert.ToDecimal(TestDb.Scalar("SELECT AmountPaid FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", secondInv.InvoiceID))),
                        "only the remainder goes toward the newer invoice — it must never be paid past its own total")
        Assert.AreEqual(1500D, CustomerBalance())
    End Sub

    <TestMethod>
    Public Sub Record_payment_never_exceeds_what_is_actually_outstanding()
        Sales.Save(Request(qty:=1, paid:=0D), _user)   ' 1000 owed, only invoice

        Dim applied = DataAccess.InTransaction(
            Function(conn, tx) As Decimal
                Return Sales.ApplyPaymentToOutstandingInvoices(conn, tx, _customer, 5000D, Date.Today, "Cash", _user)
            End Function)

        Assert.AreEqual(1000D, applied, "can't apply more against invoices than they actually owe")
    End Sub

    ' ===== a customer with no history is unaffected =====

    <TestMethod>
    Public Sub A_customer_with_no_prior_debt_behaves_exactly_as_before()
        Dim saved = Sales.Save(Request(qty:=3, paid:=3000D), _user)

        Assert.AreEqual(0D, saved.PreviousBalance)
        Assert.AreEqual(0D, saved.AppliedToPreviousBalance)
        Assert.AreEqual(0D, saved.RemainingBalance)
        Assert.AreEqual("Paid", saved.Status)
        Assert.AreEqual(0D, CustomerBalance())
    End Sub

End Class
