Imports System.Data
Imports System.Drawing
Imports System.Windows.Forms

''' Price list (Candid Purrfect only). The current price of every product at
''' each tier, the full history of price changes, and the two things done with
''' them: update prices (admin) and send the list to customers (anyone).
Public Class ucPriceList
    Inherits UserControl

    Private ReadOnly _userId As Integer
    Private ReadOnly _isAdmin As Boolean
    Private ReadOnly pnlCards As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 12, 16, 0)}
    Private ReadOnly cboView As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 210}
    Private ReadOnly txtSearch As New TextBox() With {.Width = 200}
    Private ReadOnly btnUpdate As New Button() With {.Text = "Update prices…", .AutoSize = True}
    Private ReadOnly btnSend As New Button() With {.Text = "Send price list…", .AutoSize = True}
    Private ReadOnly btnExport As New Button() With {.Text = "Export Excel", .AutoSize = True}
    Private ReadOnly toolbar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(12)}
    Private ReadOnly grid As New PagedGrid() With {.PageSize = 5}

    Public Sub New(userId As Integer, isAdmin As Boolean)
        _userId = userId
        _isAdmin = isAdmin
        cboView.Items.AddRange({"Current prices", "Price change history"})
        cboView.SelectedIndex = 0

        toolbar.Controls.Add(cboView)
        toolbar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
        toolbar.Controls.Add(txtSearch)
        If isAdmin Then
            Dim btnAdd As New Button() With {.Text = "+ Add products…", .Tag = "primary", .AutoSize = True}
            AddHandler btnAdd.Click, Sub(s, e)
                                         Using f As New frmAddPriceListProducts(_userId)
                                             If f.ShowDialog(FindForm()) = DialogResult.OK Then LoadData()
                                         End Using
                                     End Sub
            toolbar.Controls.Add(btnAdd)
            toolbar.Controls.Add(btnUpdate)
        End If
        toolbar.Controls.Add(btnSend)
        toolbar.Controls.Add(btnExport)

        Controls.Add(grid)
        Controls.Add(toolbar)
        Controls.Add(pnlCards)

        AddHandler cboView.SelectedIndexChanged, Sub(s, e)
                                                     grid.Grid.DataSource = Nothing
                                                     grid.Grid.Columns.Clear()
                                                     LoadGrid()
                                                 End Sub
        AddHandler txtSearch.TextChanged, Sub(s, e) grid.Search(txtSearch.Text)
        AddHandler btnUpdate.Click, Sub(s, e) OpenUpdate(SelectedProduct())
        AddHandler grid.Grid.CellDoubleClick, Sub(s, e) If _isAdmin AndAlso cboView.SelectedIndex = 0 Then OpenUpdate(SelectedProduct())
        AddHandler btnSend.Click, Sub(s, e)
                                      Using f As New frmSendPriceList()
                                          f.ShowDialog(FindForm())
                                      End Using
                                  End Sub
        AddHandler btnExport.Click, Sub(s, e) Exporter.SaveExcel(grid.AllRows(), "Price list",
                                                                 Company.Current.FilePrefix & If(cboView.SelectedIndex = 0, "_price_list", "_price_history"), FindForm())
        AddHandler Me.Load, Sub(s, e) LoadData()
    End Sub

    Private Function SelectedProduct() As Integer?
        If cboView.SelectedIndex <> 0 OrElse grid.Grid.SelectedRows.Count = 0 Then Return Nothing
        Return CInt(grid.Grid.SelectedRows(0).Cells("ProductID").Value)
    End Function

    Private Sub OpenUpdate(focusProduct As Integer?)
        If Not _isAdmin Then Return
        Using f As New frmPriceUpdate(_userId, focusProduct)
            If f.ShowDialog(FindForm()) = DialogResult.OK Then LoadData()
        End Using
    End Sub

    Private Sub LoadData()
        PriceBook.EnsureTables()
        pnlCards.Controls.Clear()
        Dim stats = DataAccess.GetTable(
            "SELECT (SELECT COUNT(*) FROM Products WHERE IsActive = 1) AS Products, " &
            "(SELECT COUNT(*) FROM Products WHERE IsActive = 1 AND (PriceDistributor = 0 OR PriceWholesaler = 0 OR PriceRetail = 0)) AS Unpriced, " &
            "(SELECT COUNT(*) FROM PriceChanges WHERE ChangedAt >= DATEADD(DAY, 1 - DAY(GETDATE()), CAST(GETDATE() AS DATE))) AS ThisMonth").Rows(0)
        Dim last = PriceBook.LastUpdated()
        pnlCards.Controls.Add(UiHelpers.MetricCard("Products on the list", Convert.ToInt32(stats("Products")).ToString("N0")))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Last price update", If(last.HasValue, last.Value.ToString("dd MMM yyyy HH:mm"), "Never")))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Price changes this month", Convert.ToInt32(stats("ThisMonth")).ToString("N0")))
        Dim unpriced = Convert.ToInt32(stats("Unpriced"))
        pnlCards.Controls.Add(UiHelpers.MetricCard("Missing a tier price", unpriced.ToString("N0"),
            If(unpriced > 0, Theme.Current.Danger, CType(Nothing, Color?))))
        LoadGrid()
    End Sub

    Private Sub LoadGrid()
        If cboView.SelectedIndex = 0 Then
            grid.Bind(PriceBook.CurrentPricesSql, "Category, Product",
                      hiddenColumns:={"ProductID"}, searchColumns:={"Category", "Product", "SKU"})
        Else
            grid.Bind(PriceBook.HistorySql, "ChangeID DESC",
                      hiddenColumns:={"ChangeID"}, searchColumns:={"Product", "ChangedBy", "Note"})
        End If
        If txtSearch.Text.Trim() <> "" Then grid.Search(txtSearch.Text)
    End Sub

End Class
