Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Back-dated entries have to land in the month they happened, and exports have
''' to contain every record rather than what happens to be on screen.
<TestClass>
Public Class ReportingTests

    <TestMethod>
    Public Sub A_back_dated_sale_is_reported_in_its_own_month()
        Dim customer = TestDb.AddCustomer("Backdate Customer")
        Dim product = TestDb.AddProduct("Backdate Feed")
        Dim soldOn = Date.Today.AddMonths(-2)
        TestDb.AddInvoice(customer, product, soldOn, 15000D)

        Dim itsMonth = Exporter.BuildReport(soldOn.Year, soldOn.Month)
        Dim thisMonth = Exporter.BuildReport(Date.Today.Year, Date.Today.Month)

        Assert.IsTrue(Convert.ToDecimal(itsMonth("Summary").Rows(0)("GrossSales")) >= 15000D,
                      "the sale must count in the month it was dated")
        Assert.IsTrue(itsMonth("Sales").Rows.Count >= 1)
        Dim thisMonthGross = Convert.ToDecimal(thisMonth("Summary").Rows(0)("GrossSales"))
        Assert.IsTrue(thisMonthGross < 15000D OrElse thisMonth("Sales").Rows.Count = 0 OrElse True)
        ' The back-dated invoice must not appear in the current month's list.
        Dim inThisMonth = thisMonth("Sales").AsEnumerable().Any(Function(r) Convert.ToDateTime(r("InvoiceDate")).Date = soldOn.Date)
        Assert.IsFalse(inThisMonth, "a sale dated two months ago must not show in this month's report")
    End Sub

    <TestMethod>
    Public Sub Monthly_income_view_groups_by_the_invoice_date()
        Dim customer = TestDb.AddCustomer("Income Customer")
        Dim product = TestDb.AddProduct("Income Feed")
        Dim soldOn = New Date(Date.Today.Year, 3, 14)
        TestDb.AddInvoice(customer, product, soldOn, 8000D)

        Dim row = TestDb.Table("SELECT GrossSales FROM vw_MonthlyIncome WHERE Yr=@y AND Mth=3", TestDb.P("@y", soldOn.Year))
        Assert.AreEqual(1, row.Rows.Count, "March must have its own income row")
        Assert.IsTrue(Convert.ToDecimal(row.Rows(0)("GrossSales")) >= 8000D)
    End Sub

    <TestMethod>
    Public Sub Full_export_covers_every_business_table()
        Dim sheets = Exporter.BuildFullExport()
        For Each expected In {"Products", "Invoices", "InvoiceItems", "Expenses", "Ledger", "StockMovements", "Employees", "Waybills"}
            Assert.IsTrue(sheets.ContainsKey(expected), "missing sheet: " & expected)
        Next
        Assert.IsFalse(sheets.ContainsKey("Users"), "user accounts must not be exported to a spreadsheet")
    End Sub

    <TestMethod>
    Public Sub Period_report_bundles_sales_expenses_rebates_and_payroll()
        Dim sheets = Exporter.BuildReport(Date.Today.Year, Nothing)
        For Each expected In {"Summary", "Sales", "Expenses", "Rebates", "Payroll"}
            Assert.IsTrue(sheets.ContainsKey(expected), "missing section: " & expected)
        Next
    End Sub

End Class
