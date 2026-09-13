Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data

''' Income for a chosen month or whole year: gross sales, VAT, net sales, cash
''' collected, still outstanding, COGS, expenses and estimated net profit.
''' "Save / share report" produces the full period report as PDF or Excel.
Public Class ucIncome
    Inherits UserControl

    Private ReadOnly cboYear As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 100}
    Private ReadOnly cboMonth As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 150}
    Private ReadOnly btnReport As New Button() With {.Text = "Save / share report (PDF / Excel)", .Tag = "primary", .AutoSize = True}
    Private ReadOnly pnlCards As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 12, 16, 8), .WrapContents = True}
    Private ReadOnly gridByMonth As New PagedGrid() With {.PageSize = 5}

    Public Sub New()
        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(12)}
        bar.Controls.Add(New Label() With {.Text = "Period:", .AutoSize = True, .Margin = New Padding(0, 8, 6, 0)})
        bar.Controls.Add(cboYear)
        bar.Controls.Add(cboMonth)
        bar.Controls.Add(btnReport)

        Dim gridHost As New Panel() With {.Dock = DockStyle.Fill}
        gridHost.Controls.Add(gridByMonth)
        gridHost.Controls.Add(New Label() With {.Text = "Month-by-month for the selected year", .Dock = DockStyle.Top, .Tag = "heading", .AutoSize = True, .Padding = New Padding(8)})

        Controls.Add(gridHost)
        Controls.Add(pnlCards)
        Controls.Add(bar)

        Dim thisYear = DateTime.Now.Year
        For y = thisYear To thisYear - 6 Step -1
            cboYear.Items.Add(y)
        Next
        cboYear.SelectedItem = thisYear
        cboMonth.Items.Add("Whole year")
        For m = 1 To 12
            cboMonth.Items.Add(AppInfo.MonthName(m))
        Next
        cboMonth.SelectedIndex = DateTime.Now.Month

        AddHandler cboYear.SelectedIndexChanged, Sub(s, e) Refresh2()
        AddHandler cboMonth.SelectedIndexChanged, Sub(s, e) Refresh2()
        AddHandler btnReport.Click, Sub(s, e) Exporter.RunPeriodReport(FindForm(), CInt(cboYear.SelectedItem), SelectedMonth())
        AddHandler Me.Load, Sub(s, e) Refresh2()
    End Sub

    Private Function SelectedMonth() As Integer?
        Return If(cboMonth.SelectedIndex <= 0, CType(Nothing, Integer?), cboMonth.SelectedIndex)
    End Function

    Private Sub Refresh2()
        Dim year = CInt(cboYear.SelectedItem)
        Dim month = SelectedMonth()
        Dim p As New Dictionary(Of String, Object) From {{"@y", year}, {"@m", If(month.HasValue, CObj(month.Value), DBNull.Value)}}
        Dim f = "YEAR(#C#)=@y AND (@m IS NULL OR MONTH(#C#)=@m)"

        Dim s = DataAccess.GetTable(
            "SELECT " &
            "(SELECT ISNULL(SUM(TotalAmount),0) FROM Invoices WHERE " & f.Replace("#C#", "InvoiceDate") & ") AS Gross, " &
            "(SELECT ISNULL(SUM(VATAmount),0) FROM Invoices WHERE " & f.Replace("#C#", "InvoiceDate") & ") AS Vat, " &
            "(SELECT ISNULL(SUM(AmountPaid),0) FROM Invoices WHERE " & f.Replace("#C#", "InvoiceDate") & ") AS Collected, " &
            "(SELECT ISNULL(SUM(TotalAmount-AmountPaid),0) FROM Invoices WHERE " & f.Replace("#C#", "InvoiceDate") & ") AS Outstanding, " &
            "(SELECT ISNULL(SUM(ii.Quantity*ii.UnitCost),0) FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID=ii.InvoiceID WHERE " & f.Replace("#C#", "i.InvoiceDate") & ") AS Cogs, " &
            "(SELECT ISNULL(SUM(Amount),0) FROM Expenses WHERE " & f.Replace("#C#", "ExpenseDate") & ") AS Expenses", p).Rows(0)

        Dim gross = Convert.ToDecimal(s("Gross"))
        Dim vat = Convert.ToDecimal(s("Vat"))
        Dim cogs = Convert.ToDecimal(s("Cogs"))
        Dim exp = Convert.ToDecimal(s("Expenses"))
        Dim netSales = gross - vat
        Dim netProfit = netSales - cogs - exp

        pnlCards.Controls.Clear()
        pnlCards.Controls.Add(UiHelpers.MetricCard("Gross sales", AppInfo.Money(gross)))
        pnlCards.Controls.Add(UiHelpers.MetricCard("VAT collected", AppInfo.Money(vat)))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Net sales (excl VAT)", AppInfo.Money(netSales)))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Cash collected", AppInfo.Money(s("Collected"))))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Still outstanding", AppInfo.Money(s("Outstanding"))))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Cost of goods sold", AppInfo.Money(cogs)))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Operating expenses", AppInfo.Money(exp)))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Estimated net profit", AppInfo.Money(netProfit),
            If(netProfit < 0, Theme.Current.Danger, CType(Nothing, Color?))))

        gridByMonth.Bind(DataAccess.GetTable(
            "SELECT Mth AS MonthNo, Invoices, GrossSales, VAT, NetSales, Collected, Outstanding " &
            "FROM vw_MonthlyIncome WHERE Yr=@y ORDER BY Mth",
            New Dictionary(Of String, Object) From {{"@y", year}}))
    End Sub

End Class
