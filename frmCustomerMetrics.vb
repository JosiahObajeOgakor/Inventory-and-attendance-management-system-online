Imports System.Windows.Forms

''' Read-only monthly purchase history + top products for one customer —
''' the VB equivalent of the HTML prototype's "View metrics" modal (a grid of
''' 12 rows instead of drawn bars — simpler and just as informative here).
Public Class frmCustomerMetrics
    Inherits Form

    Private lblSummary As New Label() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16)}
    Private gridMonthly As DataGridView = UiHelpers.NewGrid()
    Private gridProducts As DataGridView = UiHelpers.NewGrid()

    Public Sub New(customerId As Integer, customerName As String)
        Text = customerName & " — purchase metrics"
        Width = 520
        Height = 620
        StartPosition = FormStartPosition.CenterParent

        Dim monthly = DataAccess.GetTable(
            "SELECT DATENAME(MONTH, i.InvoiceDate) + ' ' + CAST(YEAR(i.InvoiceDate) AS VARCHAR) AS Month, " &
            "SUM(i.TotalAmount) AS Total, COUNT(*) AS Orders " &
            "FROM Invoices i WHERE i.CustomerID = @id AND i.InvoiceDate >= DATEADD(MONTH, -12, GETDATE()) " &
            "GROUP BY DATENAME(MONTH, i.InvoiceDate) + ' ' + CAST(YEAR(i.InvoiceDate) AS VARCHAR), YEAR(i.InvoiceDate), MONTH(i.InvoiceDate) " &
            "ORDER BY YEAR(i.InvoiceDate), MONTH(i.InvoiceDate)",
            New Dictionary(Of String, Object) From {{"@id", customerId}})

        Dim total12mo = monthly.AsEnumerable().Sum(Function(r) Convert.ToDecimal(r("Total")))
        Dim orders = monthly.AsEnumerable().Sum(Function(r) Convert.ToInt32(r("Orders")))
        Dim avgOrder = If(orders > 0, total12mo / orders, 0)
        Dim tier = If(total12mo >= 800000, "Gold", If(total12mo >= 400000, "Silver", "Bronze"))

        lblSummary.Text = "12-month total: ₦" & total12mo.ToString("N0") & vbCrLf &
                           "Orders: " & orders & "   ·   Avg order: ₦" & avgOrder.ToString("N0") & "   ·   Tier: " & tier

        Dim products = DataAccess.GetTable(
            "SELECT p.Name, SUM(ii.Quantity) AS UnitsPurchased " &
            "FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID = ii.InvoiceID JOIN Products p ON p.ProductID = ii.ProductID " &
            "WHERE i.CustomerID = @id AND i.InvoiceDate >= DATEADD(MONTH, -12, GETDATE()) " &
            "GROUP BY p.Name ORDER BY SUM(ii.Quantity) DESC",
            New Dictionary(Of String, Object) From {{"@id", customerId}})

        gridMonthly.DataSource = monthly
        gridProducts.DataSource = products

        Dim split As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .RowCount = 2}
        split.RowStyles.Add(New RowStyle(SizeType.Percent, 55))
        split.RowStyles.Add(New RowStyle(SizeType.Percent, 45))

        Dim topLabel As New Label() With {.Text = "Monthly purchases (last 12 months)", .Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8)}
        Dim topPanel As New Panel() With {.Dock = DockStyle.Fill}
        topPanel.Controls.Add(gridMonthly)   ' Dock.Fill first (back of z-order)…
        topPanel.Controls.Add(topLabel)      ' …then the docked header on top of it

        Dim botLabel As New Label() With {.Text = "Top products purchased", .Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8)}
        Dim botPanel As New Panel() With {.Dock = DockStyle.Fill}
        botPanel.Controls.Add(gridProducts)
        botPanel.Controls.Add(botLabel)

        split.Controls.Add(topPanel, 0, 0)
        split.Controls.Add(botPanel, 0, 1)

        Controls.Add(split)
        Controls.Add(lblSummary)
        Theme.Apply(Me)
    End Sub

End Class
