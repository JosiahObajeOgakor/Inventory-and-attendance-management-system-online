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
    Private btnLabel As New Button() With {.Text = "Print shelf label", .AutoSize = True, .Enabled = False}
    Private btnSerials As New Button() With {.Text = "Serial numbers", .AutoSize = True}
    Private btnDelete As New Button() With {.Text = "Delete item", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private toolbar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(12)}

    Public Sub New(userId As Integer, Optional isAdminUser As Boolean = False)
        currentUserId = userId
        isAdmin = isAdminUser
        ' Candid Purrfect buys everything it sells — stock comes in by purchase,
        ' never by production. ChewyPets keeps its production screens.
        If Buys Then
            btnProduction.Text = "+ Record purchase"
            cboView.Items.AddRange({"Stock by product", "Batches & expiry", "Purchase history"})
        Else
            cboView.Items.AddRange({"Stock by product", "Batches & expiry", "Production history"})
        End If
        cboView.SelectedIndex = 0

        toolbar.Controls.Add(cboView)
        toolbar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
        toolbar.Controls.Add(txtSearch)
        toolbar.Controls.Add(btnProduction)
        toolbar.Controls.Add(btnAddItem)
        If isAdmin Then toolbar.Controls.Add(btnEditPrices)
        toolbar.Controls.Add(btnLabel)
        If isAdmin Then toolbar.Controls.Add(btnSerials)
        ' Clerks can send a price list too — the Customers screen is admin-only.
        If Company.Current.HasPriceLists Then
            Dim btnPriceList As New Button() With {.Text = "Send price list…", .AutoSize = True}
            AddHandler btnPriceList.Click, Sub(s, e)
                                               Using f As New frmSendPriceList()
                                                   f.ShowDialog(FindForm())
                                               End Using
                                           End Sub
            toolbar.Controls.Add(btnPriceList)
        End If
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
        AddHandler btnExportXlsx.Click, Sub(s, e) Exporter.SaveExcel(grid.AllRows(), "Inventory", Company.Current.FilePrefix & "_inventory", FindForm())
        AddHandler btnDelete.Click, AddressOf btnDelete_Click
        AddHandler btnLabel.Click, AddressOf PrintShelfLabel
        AddHandler btnSerials.Click, Sub(s, e)
                                         Using f As New frmSerials(currentUserId)
                                             f.ShowDialog(FindForm())
                                         End Using
                                         LoadGrid()
                                     End Sub
        AddHandler grid.Grid.SelectionChanged, Sub(s, e)
                                              Dim has = grid.Grid.SelectedRows.Count > 0
                                              btnDelete.Enabled = has
                                              btnEditPrices.Enabled = has AndAlso cboView.SelectedIndex = 0
                                              btnLabel.Enabled = has AndAlso ByProduct
                                          End Sub
        AddHandler grid.PageBound, AddressOf HighlightRows
        AddHandler Me.Load, Sub(s, e) LoadGrid()
    End Sub

    Private ReadOnly Property ByProduct As Boolean
        Get
            Return cboView.SelectedIndex = 0
        End Get
    End Property

    ''' True for a business that stocks up by buying (Candid Purrfect).
    Private Shared ReadOnly Property Buys As Boolean
        Get
            Return Not Company.Current.IsHome
        End Get
    End Property

    Private Sub btnProduction_Click(sender As Object, e As EventArgs)
        If Buys Then
            ' Saving the purchase books the goods into stock in the same step.
            Using f As New frmNewPO(currentUserId)
                If f.ShowDialog(FindForm()) = DialogResult.OK Then LoadGrid()
            End Using
            Return
        End If
        Using f As New frmProduction(currentUserId)
            If f.ShowDialog(FindForm()) = DialogResult.OK Then LoadGrid()
        End Using
    End Sub

    Private Sub LoadGrid()
        Dim search = "%" & txtSearch.Text.Trim() & "%"
        Dim table As DataTable

        If ByProduct AndAlso Buys Then
            ' Same view for a buying business, with what came in by purchase this month.
            table = DataAccess.GetTable(
                "SELECT cat.Name AS Category, p.ProductID, p.SKU, p.Name, p.Unit, " &
                "ISNULL((SELECT SUM(QuantityOnHand) FROM StockBatches b WHERE b.ProductID = p.ProductID), 0) AS TotalQty, " &
                "ISNULL((SELECT SUM(poi.Quantity) FROM PurchaseOrderItems poi JOIN PurchaseOrders po ON po.POID = poi.POID " &
                "        WHERE poi.ProductID = p.ProductID AND po.Status = 'Received' " &
                "        AND YEAR(po.OrderDate)=YEAR(GETDATE()) AND MONTH(po.OrderDate)=MONTH(GETDATE())),0) AS PurchasedThisMonth, " &
                "p.ReorderLevel, p.CostPrice, p.PriceDistributor, p.PriceWholesaler, p.PriceRetail " &
                "FROM Products p JOIN Categories cat ON cat.CategoryID = p.CategoryID " &
                "WHERE p.IsActive = 1 AND (p.Name LIKE @s OR p.SKU LIKE @s) " &
                "ORDER BY cat.Name, p.Name",
                New Dictionary(Of String, Object) From {{"@s", search}})
            grid.Bind(table, hiddenColumns:={"ProductID"})
        ElseIf cboView.SelectedIndex = 2 AndAlso Buys Then
            ' What was bought, from whom, when, how many and at what cost — newest first.
            table = DataAccess.GetTable(
                "SELECT po.OrderDate AS PurchasedOn, po.PONumber AS Purchase, s.Name AS Supplier, p.SKU, p.Name AS Product, " &
                "cat.Name AS Category, poi.Quantity AS QtyBought, p.Unit, poi.UnitCost, (poi.Quantity * poi.UnitCost) AS LineCost, " &
                "po.PaymentStatus AS Payment, ISNULL(u.FullName, '') AS RecordedBy " &
                "FROM PurchaseOrderItems poi JOIN PurchaseOrders po ON po.POID = poi.POID " &
                "JOIN Suppliers s ON s.SupplierID = po.SupplierID " &
                "JOIN Products p ON p.ProductID = poi.ProductID " &
                "JOIN Categories cat ON cat.CategoryID = p.CategoryID " &
                "LEFT JOIN Users u ON u.UserID = po.CreatedByUserID " &
                "WHERE po.Status = 'Received' AND (p.Name LIKE @s OR p.SKU LIKE @s OR s.Name LIKE @s OR po.PONumber LIKE @s) " &
                "ORDER BY po.OrderDate DESC, po.POID DESC",
                New Dictionary(Of String, Object) From {{"@s", search}})
            grid.Bind(table)
        ElseIf ByProduct Then
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
            ' Lawal and Shore are ChewyPets' warehouses; another business's stock never sits in them.
            grid.Bind(table, hiddenColumns:=If(Company.Current.IsHome, {"ProductID"}, {"ProductID", "Lawal", "Shore"}))
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

    ''' A print-ready shelf label for the selected product: name, price and a
    ''' Code 128 barcode. A product added before barcodes existed gets one minted
    ''' here rather than sending the user back to the edit screen for it.
    Private Sub PrintShelfLabel(sender As Object, e As EventArgs)
        If grid.Grid.SelectedRows.Count = 0 OrElse Not ByProduct Then Return
        Dim productId = CInt(grid.Grid.SelectedRows(0).Cells("ProductID").Value)

        Dim row = DataAccess.GetTable(
            "SELECT Name, SKU, Barcode, PriceRetail FROM Products WHERE ProductID = @id",
            New Dictionary(Of String, Object) From {{"@id", productId}}).Rows(0)

        Dim barcode = Convert.ToString(row("Barcode"))
        If barcode = "" Then
            barcode = Barcodes.MintInternalBarcode(productId)
            DataAccess.Execute("UPDATE Products SET Barcode = @b WHERE ProductID = @id",
                New Dictionary(Of String, Object) From {{"@b", barcode}, {"@id", productId}})
        End If

        Try
            Using label = Barcodes.ShelfLabel(Convert.ToString(row("Name")), Convert.ToString(row("SKU")),
                                              barcode, Convert.ToDecimal(row("PriceRetail")))
                AppUI.ShowImagePreview(label, $"Shelf label — {row("Name")}", FindForm())
            End Using
        Catch ex As Exception
            AppUI.Toast("Could not build the label: " & ex.Message, AppUI.ToastKind.Error)
        End Try
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

                ' A scanned supplier code is kept as-is; an unlabelled product
                ' gets an in-store one, which needs the ID we just got back.
                Dim barcode = If(f.Barcode <> "", f.Barcode, Barcodes.MintInternalBarcode(newProductId))
                DataAccess.Execute("UPDATE Products SET Barcode = @b WHERE ProductID = @id",
                    New Dictionary(Of String, Object) From {{"@b", barcode}, {"@id", newProductId}})

                DataAccess.Execute(
                    "INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, ExpiryDate, QuantityOnHand) " &
                    "VALUES (@productId, @warehouseId, @batch, @expiry, @qty)",
                    New Dictionary(Of String, Object) From {
                        {"@productId", newProductId}, {"@warehouseId", f.WarehouseID},
                        {"@batch", f.BatchNumber}, {"@expiry", f.ExpiryDate}, {"@qty", f.Quantity}})

                AppUI.Toast($"Added {f.ProductName} — barcode {barcode}.", AppUI.ToastKind.Success)
                LoadGrid()
            End If
        End Using
    End Sub

End Class
