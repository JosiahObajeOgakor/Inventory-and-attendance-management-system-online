Imports System.Windows.Forms
Imports System.Drawing

''' Inventory. Two views:
'''   • "Stock by product" (default) — one row per product, grouped by category,
'''     showing quantity in each warehouse plus cost and the three selling
'''     prices (distributor / wholesaler / retail).
'''   • "Batches & expiry" — the per-batch detail view with low-stock / expiry
'''     highlighting and the add-item flow.
Public Class ucInventory
    Inherits UserControl

    Private ReadOnly isAdmin As Boolean
    Private ReadOnly currentUserId As Integer
    Private ReadOnly grid As New PagedGrid() With {.PageSize = 5}
    Private cboView As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 190}
    Private txtSearch As New TextBox() With {.Width = 200}
    Private btnAddItem As New Button() With {.Text = "+ Add item", .Tag = "primary", .AutoSize = True}
    ' Everyone (admin or clerk) can add what was produced.
    Private btnProduction As New Button() With {.Text = "+ Record production", .Tag = "primary", .AutoSize = True}
    Private btnEditPrices As New Button() With {.Text = "Edit prices", .AutoSize = True, .Enabled = False}
    Private btnExport As New Button() With {.Text = "Export CSV", .AutoSize = True}
    Private btnExportXlsx As New Button() With {.Text = "Export Excel", .AutoSize = True}
    Private btnDelete As New Button() With {.Text = "Delete item", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private toolbar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(12)}

    Public Sub New(userId As Integer, Optional isAdminUser As Boolean = False)
        currentUserId = userId
        isAdmin = isAdminUser
        cboView.Items.AddRange({"Stock by product", "Batches & expiry", "Production history"})
        cboView.SelectedIndex = 0

        toolbar.Controls.Add(cboView)
        toolbar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
        toolbar.Controls.Add(txtSearch)
        toolbar.Controls.Add(btnProduction)
        toolbar.Controls.Add(btnAddItem)
        If isAdmin Then toolbar.Controls.Add(btnEditPrices)
        toolbar.Controls.Add(btnExport)
        toolbar.Controls.Add(btnExportXlsx)
        If isAdmin Then toolbar.Controls.Add(btnDelete)
        Controls.Add(grid)
        Controls.Add(toolbar)

        AddHandler cboView.SelectedIndexChanged, Sub(s, e)
                                                     ' Drop the old columns so each view keeps its own order.
                                                     grid.Grid.DataSource = Nothing
                                                     grid.Grid.Columns.Clear()
                                                     LoadGrid()
                                                 End Sub
        AddHandler txtSearch.TextChanged, Sub(s, e) LoadGrid()
        AddHandler btnAddItem.Click, AddressOf btnAddItem_Click
        AddHandler btnProduction.Click, AddressOf btnProduction_Click
        AddHandler btnEditPrices.Click, Sub(s, e) EditPrices()
        AddHandler grid.Grid.CellDoubleClick, Sub(s, e) If isAdmin Then EditPrices()
        AddHandler btnExport.Click, Sub(s, e) AppUI.ExportCsv(grid.AllRows(), "inventory", FindForm())
        AddHandler btnExportXlsx.Click, Sub(s, e) Exporter.SaveExcel(grid.AllRows(), "Inventory", "ChewyPetsFeed_inventory", FindForm())
        AddHandler btnDelete.Click, AddressOf btnDelete_Click
        AddHandler grid.Grid.SelectionChanged, Sub(s, e)
                                              Dim has = grid.Grid.SelectedRows.Count > 0
                                              btnDelete.Enabled = has
                                              btnEditPrices.Enabled = has AndAlso cboView.SelectedIndex = 0
                                          End Sub
        AddHandler grid.PageBound, AddressOf HighlightRows
        AddHandler Me.Load, Sub(s, e) LoadGrid()
    End Sub

    Private ReadOnly Property ByProduct As Boolean
        Get
            Return cboView.SelectedIndex = 0
        End Get
    End Property

    Private Sub btnProduction_Click(sender As Object, e As EventArgs)
        Using f As New frmProduction(currentUserId)
            If f.ShowDialog(FindForm()) = DialogResult.OK Then LoadGrid()
        End Using
    End Sub

    Private Sub LoadGrid()
        Dim search = "%" & txtSearch.Text.Trim() & "%"
        Dim table As DataTable

        If ByProduct Then
            table = DataAccess.GetTable(
                "SELECT cat.Name AS Category, p.ProductID, p.SKU, p.Name, p.Unit, " &
                "ISNULL((SELECT SUM(QuantityOnHand) FROM StockBatches b WHERE b.ProductID = p.ProductID), 0) AS TotalQty, " &
                "ISNULL((SELECT SUM(QuantityOnHand) FROM StockBatches b JOIN Warehouses w ON w.WarehouseID=b.WarehouseID WHERE b.ProductID=p.ProductID AND w.Name='Lawal warehouse'),0) AS Lawal, " &
                "ISNULL((SELECT SUM(QuantityOnHand) FROM StockBatches b JOIN Warehouses w ON w.WarehouseID=b.WarehouseID WHERE b.ProductID=p.ProductID AND w.Name='Shore warehouse'),0) AS Shore, " &
                "ISNULL((SELECT SUM(sm.Quantity) FROM StockMovements sm WHERE sm.ProductID=p.ProductID AND sm.ReferenceType='Production' " &
                "        AND YEAR(sm.MovementDate)=YEAR(GETDATE()) AND MONTH(sm.MovementDate)=MONTH(GETDATE())),0) AS ProducedThisMonth, " &
                "p.ReorderLevel, p.CostPrice, p.PriceDistributor, p.PriceWholesaler, p.PriceRetail " &
                "FROM Products p JOIN Categories cat ON cat.CategoryID = p.CategoryID " &
                "WHERE p.IsActive = 1 AND (p.Name LIKE @s OR p.SKU LIKE @s) " &
                "ORDER BY cat.Name, p.Name",
                New Dictionary(Of String, Object) From {{"@s", search}})
            grid.Bind(table, hiddenColumns:={"ProductID"})
        ElseIf cboView.SelectedIndex = 2 Then
            ' What was produced, when, and by whom — newest first.
            table = DataAccess.GetTable(
                "SELECT sm.MovementDate AS ProducedOn, p.SKU, p.Name AS Product, cat.Name AS Category, " &
                "w.Name AS Warehouse, sm.Quantity AS QtyProduced, p.Unit, ISNULL(u.FullName, '') AS RecordedBy " &
                "FROM StockMovements sm JOIN Products p ON p.ProductID = sm.ProductID " &
                "JOIN Categories cat ON cat.CategoryID = p.CategoryID " &
                "JOIN Warehouses w ON w.WarehouseID = sm.WarehouseID " &
                "LEFT JOIN Users u ON u.UserID = sm.UserID " &
                "WHERE sm.ReferenceType = 'Production' AND (p.Name LIKE @s OR p.SKU LIKE @s) " &
                "ORDER BY sm.MovementDate DESC, sm.MovementID DESC",
                New Dictionary(Of String, Object) From {{"@s", search}})
            grid.Bind(table)
        Else
            table = DataAccess.GetTable(
                "SELECT p.ProductID, cat.Name AS Category, p.SKU, p.Name, w.Name AS Warehouse, sb.BatchNumber, sb.ExpiryDate, " &
                "sb.QuantityOnHand, p.ReorderLevel, " &
                "CASE WHEN sb.QuantityOnHand <= p.ReorderLevel THEN 'Low stock' " &
                "     WHEN sb.ExpiryDate IS NOT NULL AND DATEDIFF(DAY, GETDATE(), sb.ExpiryDate) <= 60 THEN 'Expiring soon' " &
                "     ELSE 'OK' END AS Status, " &
                "(sb.QuantityOnHand * p.CostPrice) AS StockValue " &
                "FROM StockBatches sb JOIN Products p ON p.ProductID = sb.ProductID " &
                "JOIN Categories cat ON cat.CategoryID = p.CategoryID " &
                "JOIN Warehouses w ON w.WarehouseID = sb.WarehouseID " &
                "WHERE p.Name LIKE @s OR p.SKU LIKE @s ORDER BY cat.Name, p.Name",
                New Dictionary(Of String, Object) From {{"@s", search}})
            grid.Bind(table, hiddenColumns:={"ProductID"})
        End If
    End Sub

    ''' Low stock in red, near-expiry in amber. Runs on every page the grid
    ''' shows, not just the first — the rows are rebuilt each time.
    Private Sub HighlightRows(sender As Object, e As EventArgs)
        For Each r As DataGridViewRow In grid.Grid.Rows
            If grid.Grid.Columns.Contains("Status") Then
                Select Case Convert.ToString(r.Cells("Status").Value)
                    Case "Low stock" : r.DefaultCellStyle.BackColor = ColorTranslator.FromHtml("#FEE2E2")
                    Case "Expiring soon" : r.DefaultCellStyle.BackColor = ColorTranslator.FromHtml("#FEF3C7")
                End Select
            ElseIf grid.Grid.Columns.Contains("TotalQty") AndAlso grid.Grid.Columns.Contains("ReorderLevel") Then
                If r.Cells("TotalQty").Value IsNot Nothing AndAlso r.Cells("ReorderLevel").Value IsNot Nothing AndAlso
                   Convert.ToInt32(r.Cells("TotalQty").Value) <= Convert.ToInt32(r.Cells("ReorderLevel").Value) Then
                    r.DefaultCellStyle.BackColor = ColorTranslator.FromHtml("#FEE2E2")
                End If
            End If
        Next
    End Sub

    Private Sub EditPrices()
        If grid.Grid.SelectedRows.Count = 0 OrElse Not ByProduct Then Return
        Dim productId = CInt(grid.Grid.SelectedRows(0).Cells("ProductID").Value)
        Using f As New frmProductPrices(productId)
            If f.ShowDialog(FindForm()) = DialogResult.OK Then
                AppUI.Toast("Prices updated.", AppUI.ToastKind.Success)
                LoadGrid()
            End If
        End Using
    End Sub

    Private Sub btnDelete_Click(sender As Object, e As EventArgs)
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Dim row = grid.Grid.SelectedRows(0)
        Dim productId = CInt(row.Cells("ProductID").Value)
        Dim name = Convert.ToString(row.Cells("Name").Value)
        Try
            DataAccess.Execute("DELETE FROM StockBatches WHERE ProductID = @id AND BatchNumber <> 'PO-RECEIPT'",
                               New Dictionary(Of String, Object) From {{"@id", productId}})
        Catch
        End Try
        If AppUI.TryDelete(FindForm(), $"product ""{name}""",
                           "DELETE FROM Products WHERE ProductID = @id",
                           New Dictionary(Of String, Object) From {{"@id", productId}}) Then
            LoadGrid()
        End If
    End Sub

    Private Sub btnAddItem_Click(sender As Object, e As EventArgs)
        Using f As New frmAddItem()
            If f.ShowDialog() = DialogResult.OK Then
                Dim newProductId = DataAccess.ExecuteScalarInsert(
                    "INSERT INTO Products (SKU, Name, CategoryID, Unit, ReorderLevel, CostPrice, SellingPrice, PriceRetail, PriceWholesaler, PriceDistributor) " &
                    "VALUES (@sku, @name, @categoryId, @unit, @reorder, @cost, @retail, @retail, @whole, @dist)",
                    New Dictionary(Of String, Object) From {
                        {"@sku", f.Sku}, {"@name", f.ProductName}, {"@categoryId", f.CategoryID},
                        {"@unit", f.Unit}, {"@reorder", f.ReorderLevel}, {"@cost", f.CostPrice},
                        {"@retail", f.PriceRetail}, {"@whole", f.PriceWholesaler}, {"@dist", f.PriceDistributor}})

                DataAccess.Execute(
                    "INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, ExpiryDate, QuantityOnHand) " &
                    "VALUES (@productId, @warehouseId, @batch, @expiry, @qty)",
                    New Dictionary(Of String, Object) From {
                        {"@productId", newProductId}, {"@warehouseId", f.WarehouseID},
                        {"@batch", f.BatchNumber}, {"@expiry", f.ExpiryDate}, {"@qty", f.Quantity}})

                AppUI.Toast($"Added {f.ProductName}.", AppUI.ToastKind.Success)
                LoadGrid()
            End If
        End Using
    End Sub

End Class
