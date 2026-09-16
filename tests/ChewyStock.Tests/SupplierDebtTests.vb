Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' A supplier we already owe from an earlier order must never have that old
''' debt quietly dropped by a new one — the mirror of CustomerDebtTests, with
''' the money running the other way: WE owe THEM. Today's order is settled
''' first out of whatever's paid, anything paid beyond that chips away at the
''' old debt (oldest order first), and whatever's still unpaid stays visible
''' on the supplier's balance.
<TestClass>
Public Class SupplierDebtTests

    Private _supplier As Integer
    Private _product As Integer
    Private _user As Integer

    <TestInitialize>
    Public Sub Setup()
        _supplier = DataAccess.ExecuteScalarInsert("INSERT INTO Suppliers (Name) VALUES (@n)",
            TestDb.P("@n", "Debt Supplier " & Guid.NewGuid().ToString("N").Substring(0, 5)))
        _product = TestDb.AddProduct("Debt Supply Feed " & Guid.NewGuid().ToString("N").Substring(0, 5), 600D)
        _user = TestDb.Count("SELECT MIN(UserID) FROM Users")
    End Sub

    Private Function Request(qty As Integer, paid As Decimal) As Purchasing.PurchaseRequest
        Dim req As New Purchasing.PurchaseRequest() With {
            .SupplierID = _supplier, .SupplierName = "Debt Supplier", .OrderDate = Date.Today, .PaidNow = paid}
        req.Lines.Add(New Purchasing.PurchaseLine() With {.ProductID = _product, .ProductName = "Debt Supply Feed", .Quantity = qty, .UnitCost = 1000D})
        Return req
    End Function

    Private Function SupplierBalance() As Decimal
        Return Convert.ToDecimal(TestDb.Scalar("SELECT Balance FROM Suppliers WHERE SupplierID=@s", TestDb.P("@s", _supplier)))
    End Function

    ' ===== a new purchase sees and reports the old debt =====

    <TestMethod>
    Public Sub A_new_purchase_reports_what_we_already_owed()
        Purchasing.Save(Request(qty:=2, paid:=0D), _user)   ' first order, unpaid — 2000 owed

        Dim second = Purchasing.Save(Request(qty:=1, paid:=1000D), _user)   ' second order, paid in full

        Assert.AreEqual(2000D, second.PreviousBalance, "the old debt must be read, not ignored")
        Assert.AreEqual("Paid", second.Status, "this order's own payment is what was paid")
        Assert.AreEqual(2000D, second.RemainingBalance, "the old debt is still owed — it can't just vanish")
        Assert.AreEqual(2000D, SupplierBalance(), "nothing was paid toward the old debt, so it's unchanged")
    End Sub

    <TestMethod>
    Public Sub Paying_more_than_this_orders_own_total_clears_old_debt_first_come_first_served()
        Purchasing.Save(Request(qty:=2, paid:=0D), _user)   ' 2000 owed from before

        ' This order is 1000; hand over 2500 — 1000 for it, 1500 off the old debt.
        Dim second = Purchasing.Save(Request(qty:=1, paid:=2500D), _user)

        Assert.AreEqual("Paid", second.Status)
        Assert.AreEqual(1500D, second.AppliedToPreviousBalance)
        Assert.AreEqual(500D, second.RemainingBalance, "2000 owed − 1500 paid toward it = 500 left")
        Assert.AreEqual(500D, SupplierBalance())
    End Sub

    <TestMethod>
    Public Sub Paying_enough_to_cover_everything_clears_the_whole_account()
        Purchasing.Save(Request(qty:=2, paid:=0D), _user)   ' 2000 owed

        Dim second = Purchasing.Save(Request(qty:=1, paid:=3000D), _user)   ' this order's 1000 + the old 2000

        Assert.AreEqual(0D, second.RemainingBalance)
        Assert.AreEqual(0D, SupplierBalance())
        Assert.AreEqual("Paid", second.Status)
    End Sub

    <TestMethod>
    Public Sub Overpaying_beyond_everything_owed_is_capped_not_lost_as_credit()
        Purchasing.Save(Request(qty:=2, paid:=0D), _user)   ' 2000 owed

        Dim second = Purchasing.Save(Request(qty:=1, paid:=10000D), _user)   ' far more than owed in total

        Assert.AreEqual(0D, second.RemainingBalance)
        Assert.AreEqual(0D, SupplierBalance(), "the supplier balance can't go negative from an accidental overpay")
    End Sub

    <TestMethod>
    Public Sub Not_covering_even_this_orders_own_total_leaves_the_old_debt_completely_untouched()
        Purchasing.Save(Request(qty:=2, paid:=0D), _user)   ' 2000 owed

        Dim second = Purchasing.Save(Request(qty:=1, paid:=400D), _user)   ' this order's own 1000 not even covered

        Assert.AreEqual("Partial", second.Status)
        Assert.AreEqual(0D, second.AppliedToPreviousBalance, "nothing left over to touch the old debt")
        Assert.AreEqual(2600D, second.RemainingBalance, "2000 old + 600 left on this order")
        Assert.AreEqual(2600D, SupplierBalance())
    End Sub

    ' ===== the paper trail =====

    <TestMethod>
    Public Sub Extra_payment_toward_old_debt_is_recorded_on_the_ledger_and_the_old_order()
        Dim first = Purchasing.Save(Request(qty:=2, paid:=0D), _user)   ' 2000 owed, Unpaid

        Dim second = Purchasing.Save(Request(qty:=1, paid:=3000D), _user)   ' this order's 1000 + the old 2000, fully clearing it

        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Ledger WHERE Reference = @r AND EntryType = 'Debit'",
                                        TestDb.P("@r", second.PONumber)),
                        "the payment toward old debt must be traceable back to the order that generated it")
        Assert.AreEqual("Paid", Convert.ToString(TestDb.Scalar("SELECT PaymentStatus FROM PurchaseOrders WHERE POID=@po", TestDb.P("@po", first.POID))),
                        "the OLD order itself is marked paid once its own total is covered")
    End Sub

    <TestMethod>
    Public Sub A_fully_paid_order_posts_no_ledger_entry_at_all()
        Dim saved = Purchasing.Save(Request(qty:=1, paid:=1000D), _user)   ' first order, no prior debt, fully paid

        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Ledger WHERE Reference = @r", TestDb.P("@r", saved.PONumber)),
                        "nothing is owed and nothing was paid toward old debt, so there's nothing to post")
    End Sub

    <TestMethod>
    Public Sub Two_old_orders_are_paid_off_oldest_first()
        Dim first = Purchasing.Save(Request(qty:=1, paid:=0D), _user)     ' 1000 owed, oldest
        Dim secondOld = Purchasing.Save(Request(qty:=1, paid:=0D), _user) ' another 1000 owed

        ' This order (1000) + 1500 extra — clears the oldest fully and half of
        ' the second, none left for the newest order to spare further.
        Purchasing.Save(Request(qty:=1, paid:=2500D), _user)

        Assert.AreEqual("Paid", Convert.ToString(TestDb.Scalar("SELECT PaymentStatus FROM PurchaseOrders WHERE POID=@po", TestDb.P("@po", first.POID))),
                        "the oldest debt is cleared first")
        Assert.AreEqual("Partial", Convert.ToString(TestDb.Scalar("SELECT PaymentStatus FROM PurchaseOrders WHERE POID=@po", TestDb.P("@po", secondOld.POID))))
        Assert.AreEqual(500D, Convert.ToDecimal(TestDb.Scalar("SELECT AmountPaid FROM PurchaseOrders WHERE POID=@po", TestDb.P("@po", secondOld.POID))))
        Assert.AreEqual(500D, SupplierBalance(), "1000+1000 owed − 1000(this order) − 1500(extra) = 500 left")
    End Sub

    <TestMethod>
    Public Sub Apply_payment_never_exceeds_what_is_actually_outstanding()
        Purchasing.Save(Request(qty:=1, paid:=0D), _user)   ' 1000 owed, only order

        Dim applied = DataAccess.InTransaction(
            Function(conn, tx) As Decimal
                Return Purchasing.ApplyPaymentToOutstandingPurchaseOrders(conn, tx, _supplier, 5000D)
            End Function)

        Assert.AreEqual(1000D, applied, "can't apply more against orders than they actually owe")
    End Sub

    ' ===== "Mark paid" on the Suppliers screen only clears the actual remainder =====

    <TestMethod>
    Public Sub Marking_paid_clears_only_the_remaining_balance_not_the_full_total_again()
        Dim saved = Purchasing.Save(Request(qty:=2, paid:=1200D), _user)   ' Total 2000, 1200 paid, 800 left, Partial
        Assert.AreEqual(800D, SupplierBalance())

        ' Mirrors ucSuppliers' btnMarkPaid_Click.
        DataAccess.InTransaction(
            Function(conn, tx) As Boolean
                Dim outstanding = 2000D - 1200D
                DataAccess.Exec(conn, tx, "UPDATE PurchaseOrders SET AmountPaid = TotalAmount, PaymentStatus = 'Paid' WHERE POID = @id",
                                TestDb.P("@id", saved.POID))
                DataAccess.Exec(conn, tx, "UPDATE Suppliers SET Balance = Balance - @amt WHERE SupplierID = @sid",
                                TestDb.P("@amt", outstanding, "@sid", _supplier))
                Return True
            End Function)

        Assert.AreEqual(0D, SupplierBalance(), "only the 800 actually outstanding comes off — not the full 2000 again")
        Assert.AreEqual("Paid", Convert.ToString(TestDb.Scalar("SELECT PaymentStatus FROM PurchaseOrders WHERE POID=@po", TestDb.P("@po", saved.POID))))
    End Sub

    ' ===== a supplier with no history is unaffected =====

    <TestMethod>
    Public Sub A_supplier_with_no_prior_debt_behaves_exactly_as_before()
        Dim saved = Purchasing.Save(Request(qty:=3, paid:=3000D), _user)

        Assert.AreEqual(0D, saved.PreviousBalance)
        Assert.AreEqual(0D, saved.AppliedToPreviousBalance)
        Assert.AreEqual(0D, saved.RemainingBalance)
        Assert.AreEqual("Paid", saved.Status)
        Assert.AreEqual(0D, SupplierBalance())
    End Sub

    <TestMethod>
    Public Sub Not_paying_at_all_behaves_exactly_as_the_old_unpaid_flow_did()
        Dim saved = Purchasing.Save(Request(qty:=5, paid:=0D), _user)

        Assert.AreEqual(5000D, saved.Total)
        Assert.AreEqual("Unpaid", saved.Status)
        Assert.AreEqual(5000D, SupplierBalance())
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Ledger WHERE Reference=@r", TestDb.P("@r", saved.PONumber)))
    End Sub

End Class
