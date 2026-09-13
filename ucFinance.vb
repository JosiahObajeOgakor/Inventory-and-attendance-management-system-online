Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data

''' Finance (P&amp;L summary + the credit/debit ledger), Admin-only. Revenue/COGS/
''' gross profit come from vw_ProfitAndLoss (exact, since InvoiceItems.UnitCost
''' is captured at sale time — see frmNewInvoice). Net profit subtracts this
''' month's Expenses.
'''
''' Operating expenses used to sit on this screen too, which made it two cramped
''' tables fighting for one window — and duplicated the Expenses tab, which is a
''' better expenses screen (month/category filters, its own totals). So expenses
''' live there now and the ledger gets this screen to itself. The exports are
''' behind one "Export" menu rather than a row of buttons.
Public Class ucFinance
    Inherits UserControl

    Private ReadOnly currentUserId As Integer
    Private pnlCards As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16)}
    Private ReadOnly pgLedger As New PagedGrid() With {.PageSize = 5}
    Private btnDeleteLedger As New Button() With {.Text = "Delete entry", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private btnExport As New Button() With {.Text = "Export ▾", .AutoSize = True}
    Private ReadOnly exportMenu As New ContextMenuStrip()
    Private txtSearchLedger As New TextBox() With {.Width = 160}

    Public Sub New(userId As Integer)
        currentUserId = userId

        Dim toolbar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8)}
        toolbar.Controls.Add(New Label() With {.Text = "Credit and debit ledger", .Tag = "heading", .AutoSize = True, .Margin = New Padding(0, 6, 20, 0)})
        toolbar.Controls.Add(btnDeleteLedger)
        toolbar.Controls.Add(btnExport)
        toolbar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(16, 8, 6, 0)})
        toolbar.Controls.Add(txtSearchLedger)

        Controls.Add(pgLedger)
        Controls.Add(toolbar)
        Controls.Add(pnlCards)

        exportMenu.Items.Add("Ledger (CSV)…", Nothing,
            Sub(s, e) AppUI.ExportCsv(pgLedger.AllRows(), "ledger", FindForm()))
        exportMenu.Items.Add("Ledger (Excel)…", Nothing,
            Sub(s, e) Exporter.SaveExcel(pgLedger.AllRows(), "Ledger", "ledger_" & Stamp(), FindForm()))
        exportMenu.Items.Add("All finance — expenses + ledger (Excel)…", Nothing,
            Sub(s, e) ExportAllFinance())

        AddHandler btnExport.Click, Sub(s, e) exportMenu.Show(btnExport, New Point(0, btnExport.Height))
        AddHandler btnDeleteLedger.Click, AddressOf btnDeleteLedger_Click
        AddHandler pgLedger.Grid.SelectionChanged, Sub(s, e) btnDeleteLedger.Enabled = pgLedger.Grid.SelectedRows.Count > 0
        AddHandler txtSearchLedger.TextChanged, Sub(s, e) pgLedger.Search(txtSearchLedger.Text)
        AddHandler Me.Load, Sub(s, e) LoadData()
    End Sub

    Private Shared Function Stamp() As String
        Return DateTime.Now.ToString("yyyyMMdd_HHmm")
    End Function

    ''' The ledger is this screen's table, but "all finance" still means the
    ''' expenses alongside it — read straight from the table, since expenses
    ''' are shown on their own tab now.
    Private Sub ExportAllFinance()
        Exporter.SaveExcel(New Dictionary(Of String, DataTable) From {
            {"Expenses", DataAccess.GetTable("SELECT ExpenseDate, Category, Amount, Note FROM Expenses ORDER BY ExpenseDate DESC")},
            {"Ledger", pgLedger.AllRows()}},
            "finance_" & Stamp(), FindForm())
    End Sub

    ''' Removes a credit/debit ledger line (e.g. a test entry). The ledger is a
    ''' history list, so deleting a line changes no customer or supplier balance.
    Private Sub btnDeleteLedger_Click(sender As Object, e As EventArgs)
        If pgLedger.Grid.SelectedRows.Count = 0 Then Return
        Dim row = pgLedger.Grid.SelectedRows(0)
        Dim id = CInt(row.Cells("LedgerID").Value)
        Dim label = $"{row.Cells("EntryType").Value} of ₦{Convert.ToDecimal(row.Cells("Amount").Value):N2} for {row.Cells("AccountName").Value}"
        If AppUI.TryDelete(FindForm(), label,
                           "DELETE FROM Ledger WHERE LedgerID = @id",
                           New Dictionary(Of String, Object) From {{"@id", id}}) Then
            pgLedger.Reload()
        End If
    End Sub

    Private Sub LoadData()
        pnlCards.Controls.Clear()
        Dim pl = DataAccess.GetTable("SELECT TOP 1 Revenue, COGS, GrossProfit FROM vw_ProfitAndLoss ORDER BY Yr DESC, Mth DESC")
        Dim revenue As Decimal = If(pl.Rows.Count > 0, Convert.ToDecimal(pl.Rows(0)("Revenue")), 0)
        Dim cogs As Decimal = If(pl.Rows.Count > 0, Convert.ToDecimal(pl.Rows(0)("COGS")), 0)
        Dim grossProfit As Decimal = If(pl.Rows.Count > 0, Convert.ToDecimal(pl.Rows(0)("GrossProfit")), 0)
        Dim expensesTotal = Convert.ToDecimal(DataAccess.GetTable(
            "SELECT ISNULL(SUM(Amount),0) AS Total FROM Expenses WHERE MONTH(ExpenseDate)=MONTH(GETDATE()) AND YEAR(ExpenseDate)=YEAR(GETDATE())").Rows(0)("Total"))
        Dim ap = Convert.ToDecimal(DataAccess.GetTable("SELECT ISNULL(SUM(AmountOwed),0) AS Total FROM vw_AccountsPayable").Rows(0)("Total"))
        Dim ar = Convert.ToDecimal(DataAccess.GetTable("SELECT ISNULL(SUM(Balance),0) AS Total FROM Customers").Rows(0)("Total"))

        Dim net = grossProfit - expensesTotal
        pnlCards.Controls.Add(UiHelpers.MetricCard("Revenue (month)", Fmt(revenue)))
        pnlCards.Controls.Add(UiHelpers.MetricCard("COGS (month)", Fmt(cogs)))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Gross profit", Fmt(grossProfit)))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Expenses (month)", Fmt(expensesTotal)))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Net profit (month)", Fmt(net),
            If(net < 0, Theme.Current.Danger, CType(Nothing, Color?))))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Owed to suppliers (AP)", Fmt(ap)))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Owed to us (AR)", Fmt(ar)))

        pgLedger.Bind("SELECT LedgerID, EntryDate, AccountName, AccountType, EntryType, Amount, Reference FROM Ledger",
                      "LedgerID DESC", hiddenColumns:={"LedgerID"}, searchColumns:={"AccountName", "EntryType", "Reference"})
    End Sub

    Private Function Fmt(v As Decimal) As String
        Return "₦" & v.ToString("N0")
    End Function

End Class
