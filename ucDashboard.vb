Imports System.Windows.Forms
Imports System.Drawing

''' Dashboard: brand header + summary cards + recent invoices, plus — for an
''' admin — the advice panel and reorder table that used to be their own
''' "Insights" tab. They belong here: they're what you want to see on opening
''' the app, not a screen you have to remember to go and look at. See
''' Insights.vb for what each suggestion is reading and why.
'''
''' A clerk still gets the plain low-stock list instead; the forecast and the
''' business advice are an admin's job.
Public Class ucDashboard
    Inherits UserControl

    ''' Five rows a table — enough to see what's happening at a glance, and
    ''' short enough that the tables page themselves instead of pushing the
    ''' whole dashboard into one long scroll.
    Private Const RowsPerTable As Integer = 5

    Private ReadOnly userId As Integer
    Private ReadOnly isAdmin As Boolean
    Private ReadOnly pnlCards As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 12, 16, 12), .WrapContents = True}
    Private ReadOnly pgLowStock As New PagedGrid() With {.PageSize = RowsPerTable}
    Private ReadOnly pgRecentInvoices As New PagedGrid() With {.PageSize = RowsPerTable}
    ' Admin only: the advice strip and the reorder forecast behind it.
    Private ReadOnly pnlAdvice As New FlowLayoutPanel() With {
        .Dock = DockStyle.Top, .Height = 132, .WrapContents = False, .AutoScroll = True,
        .Padding = New Padding(16, 0, 16, 10), .Visible = False}
    Private ReadOnly pgForecast As New PagedGrid() With {.PageSize = RowsPerTable}

    Public Sub New(currentUserId As Integer, isAdminUser As Boolean)
        userId = currentUserId
        isAdmin = isAdminUser
        pnlAdvice.Visible = isAdmin

        Dim split As New TableLayoutPanel()
        split.Dock = DockStyle.Fill
        split.ColumnCount = 2
        split.RowCount = 1
        split.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 45))
        split.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 55))

        ' A Dock.Fill control must be added BEFORE the docked-edge controls so it
        ' takes the leftover space instead of covering them (WinForms lays out
        ' docked children back-to-front in z-order).
        ' An admin gets the reorder forecast here — it says everything the plain
        ' low-stock list said, plus how fast each line is selling and how much to
        ' order, so showing both would just be the same answer twice.
        Dim leftPanel As New Panel() With {.Dock = DockStyle.Fill}
        If isAdmin Then
            leftPanel.Controls.Add(pgForecast)
            leftPanel.Controls.Add(New Label() With {
                .Text = "What to reorder — 6-week sales trend vs stock on hand",
                .Dock = DockStyle.Top, .Tag = "heading", .Padding = New Padding(8), .AutoSize = True})
        Else
            leftPanel.Controls.Add(pgLowStock)
            leftPanel.Controls.Add(New Label() With {.Text = "Low stock alerts", .Dock = DockStyle.Top, .Tag = "heading", .Padding = New Padding(8), .AutoSize = True})
        End If

        Dim rightPanel As New Panel() With {.Dock = DockStyle.Fill}
        rightPanel.Controls.Add(pgRecentInvoices)
        rightPanel.Controls.Add(New Label() With {.Text = "Recent invoices", .Dock = DockStyle.Top, .Tag = "heading", .Padding = New Padding(8), .AutoSize = True})

        split.Controls.Add(leftPanel, 0, 0)
        split.Controls.Add(rightPanel, 1, 0)

        ' No brand bar here: the window's own header already shows the logo and
        ' the name, and repeating them just cost a strip of the screen.
        ' Added Fill-first, then the Top bands — last added sits highest.
        Controls.Add(split)
        Controls.Add(pnlAdvice)
        Controls.Add(pnlCards)

        AddHandler Me.Load, Sub(s, e) LoadData()
        AddHandler Theme.ThemeChanged, Sub(s, e)
                                           If Not IsDisposed Then LoadData()
                                       End Sub
    End Sub

    Private Sub LoadData()
        pnlCards.Controls.Clear()
        Dim stockValue = DataAccess.GetTable("SELECT ISNULL(SUM(StockValue),0) AS Total FROM vw_StockValuation").Rows(0)("Total")
        AddCard("Total stock value", FormatCurrency(stockValue))

        AddCard("Low stock items",
                Convert.ToString(DataAccess.GetTable("SELECT COUNT(*) AS Cnt FROM vw_LowStock WHERE Status = 'Low stock'").Rows(0)("Cnt")))
        If isAdmin Then
            pgForecast.Bind(Insights.ReorderForecast())
            LoadAdvice()
        Else
            pgLowStock.Bind("SELECT Name, Warehouse, QuantityOnHand, ReorderLevel FROM vw_LowStock WHERE Status = 'Low stock'", "Name")
        End If

        Dim todaysInvoices = DataAccess.GetTable("SELECT COUNT(*) AS Cnt FROM Invoices WHERE InvoiceDate = CAST(GETDATE() AS DATE)").Rows(0)("Cnt")
        AddCard("Today's invoices", todaysInvoices.ToString())

        If isAdmin Then
            Dim pl = DataAccess.GetTable("SELECT TOP 1 GrossProfit FROM vw_ProfitAndLoss ORDER BY Yr DESC, Mth DESC")
            Dim expenses = DataAccess.GetTable("SELECT ISNULL(SUM(Amount),0) AS Total FROM Expenses WHERE MONTH(ExpenseDate)=MONTH(GETDATE()) AND YEAR(ExpenseDate)=YEAR(GETDATE())").Rows(0)("Total")
            Dim grossProfit As Decimal = If(pl.Rows.Count > 0, Convert.ToDecimal(pl.Rows(0)("GrossProfit")), 0)
            AddCard("Net profit (month, est.)", FormatCurrency(grossProfit - Convert.ToDecimal(expenses)))
        Else
            ShowAttendance()
        End If

        ' Paged rather than a fixed top-5, so the newest are in front but the
        ' rest are a click away instead of gone.
        pgRecentInvoices.Bind(
            "SELECT i.InvoiceID, i.InvoiceNumber, c.Name AS Customer, i.TotalAmount, i.[Status] " &
            "FROM Invoices i JOIN Customers c ON c.CustomerID = i.CustomerID",
            "i.InvoiceID DESC", hiddenColumns:={"InvoiceID"})
    End Sub

    Private Sub AddCard(title As String, value As String)
        pnlCards.Controls.Add(UiHelpers.MetricCard(title, value))
    End Sub

    ''' The advice strip: what the numbers are saying and what to do about it.
    ''' Never let a bad rule take the whole Dashboard down with it — an admin
    ''' opening the app to a crash instead of their figures is far worse than
    ''' opening it to one missing panel.
    Private Sub LoadAdvice()
        pnlAdvice.Controls.Clear()
        Dim advice As List(Of Insights.Recommendation)
        Try
            advice = Insights.Recommendations()
        Catch
            pnlAdvice.Visible = False
            Return
        End Try

        pnlAdvice.Visible = True
        If advice.Count = 0 Then
            pnlAdvice.Controls.Add(AdviceCard(New Insights.Recommendation With {
                .Kind = "Good", .Title = "Nothing needs your attention",
                .Detail = "No stock about to run out, no overdue payments, and no sudden drop in sales."}))
            Return
        End If
        For Each item In advice
            pnlAdvice.Controls.Add(AdviceCard(item))
        Next
    End Sub

    ''' One suggestion: a coloured spine by urgency, the finding, then the advice.
    Private Function AdviceCard(item As Insights.Recommendation) As Control
        Dim accent = AccentFor(item.Kind)

        Dim text As New TableLayoutPanel() With {
            .ColumnCount = 1, .RowCount = 2, .Dock = DockStyle.Fill, .AutoSize = False,
            .Padding = New Padding(12, 10, 12, 10), .BackColor = Theme.Current.Surface, .Margin = New Padding(0)}
        text.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        text.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        text.Controls.Add(New Label() With {
            .Text = item.Title, .Dock = DockStyle.Top, .AutoSize = True, .Tag = "keepfont",
            .MaximumSize = New Size(300, 0), .ForeColor = Theme.Current.TextPrimary,
            .Font = New Font("Segoe UI", Theme.BaseFontSize, FontStyle.Bold)}, 0, 0)
        text.Controls.Add(New Label() With {
            .Text = item.Detail, .Dock = DockStyle.Fill, .AutoSize = False, .Tag = "keepfont",
            .MaximumSize = New Size(300, 0), .ForeColor = Theme.Current.TextMuted,
            .Font = New Font("Segoe UI", Math.Max(8, Theme.BaseFontSize - 1))}, 0, 1)

        Dim card As New Panel() With {
            .Width = 330, .Height = 108, .Margin = New Padding(0, 0, 12, 0), .BackColor = accent}
        text.Left = 4
        text.Top = 0
        text.Width = card.Width - 4
        text.Height = card.Height
        card.Controls.Add(text)
        Return card
    End Function

    Private Shared Function AccentFor(kind As String) As Color
        Select Case kind
            Case "Urgent" : Return Theme.Current.Danger
            Case "Watch" : Return Theme.Current.Primary
            Case Else : Return Theme.Current.GridLineColor
        End Select
    End Function

    ''' Her own today's check-in, as a card alongside the rest.
    Private Sub ShowAttendance()
        Try
            Dim rec = Attendance.TodayRecord(userId)
            Dim inAt As DateTime? = If(rec Is Nothing OrElse rec("CheckInAt") Is DBNull.Value, Nothing, CType(Convert.ToDateTime(rec("CheckInAt")), DateTime?))
            AddCard("Your day", If(inAt.HasValue, $"Checked in {inAt.Value:HH:mm}", "Not checked in today"))
        Catch
            ' Attendance is informational here — never let it break the Dashboard.
        End Try
    End Sub

    Private Function FormatCurrency(value As Object) As String
        Return "₦" & Convert.ToDecimal(value).ToString("N0")
    End Function

End Class
