Imports System.IO
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Data safety: a sale or purchase either saves completely or not at all, the
''' daily backup runs once a day and keeps a limited history, and crashes are
''' written to a log instead of disappearing.
<TestClass>
Public Class DataSafetyTests

    Private _customer As Integer
    Private _product As Integer
    Private _warehouse As Integer
    Private _user As Integer

    <TestInitialize>
    Public Sub Setup()
        _customer = TestDb.AddCustomer("Safety Customer " & Guid.NewGuid().ToString("N").Substring(0, 5))
        _product = TestDb.AddProduct("Safety Feed " & Guid.NewGuid().ToString("N").Substring(0, 5), 600D)
        _warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")
        _user = TestDb.Count("SELECT MIN(UserID) FROM Users")
        TestDb.Exec("INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p, @w, 'SAFE', 100)",
                    TestDb.P("@p", _product, "@w", _warehouse))
    End Sub

    Private Function Request(Optional qty As Integer = 3, Optional paid As Decimal = 3000D) As Sales.SaleRequest
        Dim req As New Sales.SaleRequest() With {
            .CustomerID = _customer, .CustomerName = "Safety Customer", .CustomerType = "Retailer",
            .SaleDate = Date.Today.AddDays(-4), .PriceTier = "Retailer", .WarehouseID = _warehouse,
            .PaymentMethod = "Cash", .PaidNow = paid, .DueDate = Date.Today.AddDays(20)}
        req.Lines.Add(New Sales.SaleLine() With {.ProductID = _product, .ProductName = "Safety Feed", .Quantity = qty, .UnitPrice = 1000D, .UnitCost = 600D})
        Return req
    End Function

    <TestMethod>
    Public Sub A_sale_writes_invoice_lines_stock_payment_and_rebate_together()
        Dim saved = Sales.Save(Request(), _user)

        Assert.AreEqual(3000D, saved.Total)
        Assert.AreEqual("Paid", saved.Status)
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM InvoiceItems WHERE InvoiceID=@i", TestDb.P("@i", saved.InvoiceID)))
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Payments WHERE InvoiceID=@i", TestDb.P("@i", saved.InvoiceID)))
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM StockMovements WHERE ReferenceType='Invoice' AND ReferenceID=@i", TestDb.P("@i", saved.InvoiceID)))
        Assert.AreEqual(97, Stock.QuantityInWarehouse(_product, _warehouse), "stock comes off the shelf")
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM RebateEntries WHERE InvoiceID=@i", TestDb.P("@i", saved.InvoiceID)))
    End Sub

    <TestMethod>
    Public Sub A_credit_sale_raises_the_customer_balance_and_the_ledger()
        Dim before = Convert.ToDecimal(TestDb.Scalar("SELECT Balance FROM Customers WHERE CustomerID=@c", TestDb.P("@c", _customer)))
        Dim saved = Sales.Save(Request(qty:=2, paid:=0D), _user)

        Assert.AreEqual("Unpaid", saved.Status)
        Assert.AreEqual(before + 2000D, Convert.ToDecimal(TestDb.Scalar("SELECT Balance FROM Customers WHERE CustomerID=@c", TestDb.P("@c", _customer))))
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Ledger WHERE Reference=@r", TestDb.P("@r", saved.InvoiceNumber)))
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Payments WHERE InvoiceID=@i", TestDb.P("@i", saved.InvoiceID)))
    End Sub

    <TestMethod>
    Public Sub A_sale_that_fails_part_way_leaves_nothing_behind()
        Dim invoicesBefore = TestDb.Count("SELECT COUNT(*) FROM Invoices")
        Dim itemsBefore = TestDb.Count("SELECT COUNT(*) FROM InvoiceItems")
        Dim movesBefore = TestDb.Count("SELECT COUNT(*) FROM StockMovements")
        Dim stockBefore = Stock.QuantityInWarehouse(_product, _warehouse)
        Dim balanceBefore = Convert.ToDecimal(TestDb.Scalar("SELECT Balance FROM Customers WHERE CustomerID=@c", TestDb.P("@c", _customer)))

        ' Second line points at a product that doesn't exist: the insert fails
        ' after the invoice header and first line have already been written.
        Dim req = Request(qty:=2, paid:=0D)
        req.Lines.Add(New Sales.SaleLine() With {.ProductID = 999999, .ProductName = "Ghost", .Quantity = 1, .UnitPrice = 500D, .UnitCost = 0D})

        Try
            Sales.Save(req, _user)
            Assert.Fail("the save should have failed")
        Catch ex As Exception
            ' expected
        End Try

        Assert.AreEqual(invoicesBefore, TestDb.Count("SELECT COUNT(*) FROM Invoices"), "no half-written invoice")
        Assert.AreEqual(itemsBefore, TestDb.Count("SELECT COUNT(*) FROM InvoiceItems"), "no orphan invoice lines")
        Assert.AreEqual(movesBefore, TestDb.Count("SELECT COUNT(*) FROM StockMovements"), "no stock history for a sale that never happened")
        Assert.AreEqual(stockBefore, Stock.QuantityInWarehouse(_product, _warehouse), "stock must not be deducted")
        Assert.AreEqual(balanceBefore, Convert.ToDecimal(TestDb.Scalar("SELECT Balance FROM Customers WHERE CustomerID=@c", TestDb.P("@c", _customer))),
                        "the customer must not be charged")
    End Sub

    <TestMethod>
    Public Sub Several_sales_entered_quickly_get_their_own_numbers()
        Dim numbers = New List(Of String)
        For i = 1 To 5
            numbers.Add(Sales.Save(Request(qty:=1, paid:=1000D), _user).InvoiceNumber)
        Next
        Assert.AreEqual(5, numbers.Distinct().Count(), "each sale needs its own invoice number: " & String.Join(", ", numbers))
        For Each n In numbers
            Assert.IsTrue(n.StartsWith("ChewyStock-"), "number should carry the ChewyStock brand: " & n)
        Next
    End Sub

    <TestMethod>
    Public Sub Vat_is_only_added_when_a_rate_is_given()
        Dim without = Sales.Totals(Request())
        Assert.AreEqual(0D, without.VatAmount)

        Dim req = Request()
        req.VatRate = 7.5D
        Dim with_ = Sales.Totals(req)
        Assert.AreEqual(225D, with_.VatAmount)
        Assert.AreEqual(3225D, with_.Total)
    End Sub

    <TestMethod>
    Public Sub A_purchase_order_saves_header_lines_and_ledger_together()
        Dim supplier = TestDb.Count("SELECT MIN(SupplierID) FROM Suppliers")
        Dim req As New Purchasing.PurchaseRequest() With {
            .SupplierID = supplier, .SupplierName = "Test Supplier", .OrderDate = Date.Today.AddDays(-7)}
        req.Lines.Add(New Purchasing.PurchaseLine() With {.ProductID = _product, .Quantity = 10, .UnitCost = 500D})

        Dim saved = Purchasing.Save(req, _user)

        Assert.AreEqual(5000D, saved.Total)
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM PurchaseOrderItems WHERE POID=@p", TestDb.P("@p", saved.POID)))
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Ledger WHERE Reference=@r", TestDb.P("@r", saved.PONumber)))
        Assert.AreEqual(Date.Today.AddDays(-7),
                        Convert.ToDateTime(TestDb.Scalar("SELECT OrderDate FROM PurchaseOrders WHERE POID=@p", TestDb.P("@p", saved.POID))).Date)
    End Sub

    <TestMethod>
    Public Sub A_purchase_order_that_fails_leaves_nothing_behind()
        Dim before = TestDb.Count("SELECT COUNT(*) FROM PurchaseOrders")
        Dim ledgerBefore = TestDb.Count("SELECT COUNT(*) FROM Ledger")
        Dim req As New Purchasing.PurchaseRequest() With {
            .SupplierID = TestDb.Count("SELECT MIN(SupplierID) FROM Suppliers"), .SupplierName = "Test Supplier"}
        req.Lines.Add(New Purchasing.PurchaseLine() With {.ProductID = 999999, .Quantity = 1, .UnitCost = 100D})

        Try
            Purchasing.Save(req, _user)
            Assert.Fail("the save should have failed")
        Catch ex As Exception
            ' expected
        End Try

        Assert.AreEqual(before, TestDb.Count("SELECT COUNT(*) FROM PurchaseOrders"))
        Assert.AreEqual(ledgerBefore, TestDb.Count("SELECT COUNT(*) FROM Ledger"))
    End Sub

    ' ===== automatic backups =====

    <TestMethod>
    Public Sub The_daily_backup_runs_once_a_day_and_keeps_a_history()
        TestDb.Exec("DELETE FROM AppSettings WHERE SettingKey = 'backup.lastDate'")
        Try
            Dim first = BackupService.RunDailyBackup()
            Assert.AreNotEqual("", first, "a backup should have been written: " & BackupService.LastError)
            Assert.IsTrue(File.Exists(first))
            Assert.IsTrue(New FileInfo(first).Length > 100000, "a real backup is not tiny")

            Dim second = BackupService.RunDailyBackup()
            Assert.AreEqual("", second, "only one automatic backup per day")

            Assert.IsNotNull(BackupService.LatestBackup())
            Assert.IsTrue(Directory.GetFiles(BackupService.BackupFolder(), "ChewyStock_*.bak").Length <= BackupService.KeepCount,
                          "old backups are pruned")
        Finally
            Try
                Directory.Delete(BackupService.BackupFolder(), True)
            Catch
            End Try
            TestDb.Exec("DELETE FROM AppSettings WHERE SettingKey = 'backup.lastDate'")
        End Try
    End Sub

    ' ===== crash logging =====

    <TestMethod>
    Public Sub Unexpected_errors_are_written_to_a_log_file()
        Dim logPath = CrashLog.Write(New InvalidOperationException("test failure, not a real crash"), "test")
        Assert.AreNotEqual("", logPath, "the crash log must be written")
        Try
            Dim text = File.ReadAllText(logPath)
            Assert.IsTrue(text.Contains("test failure, not a real crash"))
            Assert.IsTrue(text.Contains("InvalidOperationException"), "the log needs the detail support would ask for")
        Finally
            Try
                File.Delete(logPath)
            Catch
            End Try
        End Try
    End Sub

End Class
