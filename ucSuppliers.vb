Imports System.Windows.Forms
Imports System.Drawing

''' One table per screen, full width — the Suppliers tab shows who we buy from
''' (with what we owe each), the Purchases tab shows the orders placed with them
''' plus the receive/pay actions. They were one cramped two-table screen before;
''' both lists are too wide to share a window, and the nav already had a tab for
''' each, so each tab now gets the whole screen.
Public Class ucSuppliers
    Inherits UserControl

    Private ReadOnly currentUserId As Integer
    Private ReadOnly showPurchaseOrders As Boolean
    Private ReadOnly gridSuppliers As New PagedGrid() With {.PageSize = 5}
    Private ReadOnly gridPurchaseOrders As New PagedGrid() With {.PageSize = 5}
    Private btnNewPO As New Button() With {.Text = "+ New purchase order", .Tag = "primary", .AutoSize = True}
    Private btnReceive As New Button() With {.Text = "Receive", .AutoSize = True, .Enabled = False}
    Private btnMarkPaid As New Button() With {.Text = "Mark paid", .AutoSize = True, .Enabled = False}
    Private btnAddSupplier As New Button() With {.Text = "+ Add supplier", .Tag = "primary", .AutoSize = True}
    Private btnEditSupplier As New Button() With {.Text = "Edit", .AutoSize = True, .Enabled = False}
    Private btnExportSuppliers As New Button() With {.Text = "Export CSV", .AutoSize = True}
    Private btnDeleteSupplier As New Button() With {.Text = "Delete supplier", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private btnExportPOs As New Button() With {.Text = "Export CSV", .AutoSize = True}
    Private lblAP As New Label() With {.AutoSize = True, .Tag = "keepfont", .Font = New Font("Segoe UI", 11, FontStyle.Bold)}
    Private txtSearchSuppliers As New TextBox() With {.Width = 130}
    Private txtSearchPOs As New TextBox() With {.Width = 130}

    ''' `purchaseOrdersOnly` picks which of the two tabs this instance is:
    ''' False = Suppliers, True = Purchases. Either way one table fills the
    ''' screen, so no column ever gets squeezed off the right edge.
    Public Sub New(userId As Integer, Optional purchaseOrdersOnly As Boolean = False)
        currentUserId = userId
        showPurchaseOrders = purchaseOrdersOnly

        Dim toolbar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8)}
        If showPurchaseOrders Then
            toolbar.Controls.Add(New Label() With {.Text = "Purchase orders", .Tag = "heading", .AutoSize = True, .Margin = New Padding(0, 6, 20, 0)})
            toolbar.Controls.Add(btnNewPO)
            toolbar.Controls.Add(btnReceive)
            toolbar.Controls.Add(btnMarkPaid)
            toolbar.Controls.Add(btnExportPOs)
            toolbar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
            toolbar.Controls.Add(txtSearchPOs)
            toolbar.Controls.Add(lblAP)
            Controls.Add(gridPurchaseOrders)
        Else
            toolbar.Controls.Add(New Label() With {.Text = "Suppliers", .Tag = "heading", .AutoSize = True, .Margin = New Padding(0, 6, 20, 0)})
            toolbar.Controls.Add(btnAddSupplier)
            toolbar.Controls.Add(btnEditSupplier)
            toolbar.Controls.Add(btnExportSuppliers)
            toolbar.Controls.Add(btnDeleteSupplier)
            toolbar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
            toolbar.Controls.Add(txtSearchSuppliers)
            Controls.Add(gridSuppliers)
        End If
        Controls.Add(toolbar)

        AddHandler gridPurchaseOrders.Grid.SelectionChanged, Sub(s, e) UpdatePoButtons()
        AddHandler gridSuppliers.Grid.SelectionChanged, Sub(s, e)
                                                      Dim has = gridSuppliers.Grid.SelectedRows.Count > 0
                                                      btnDeleteSupplier.Enabled = has
                                                      btnEditSupplier.Enabled = has
                                                  End Sub
        AddHandler gridSuppliers.Grid.CellDoubleClick, Sub(s, e) EditSupplier()
        AddHandler btnAddSupplier.Click, Sub(s, e)
                                             Using f As New frmAddSupplier()
                                                 If f.ShowDialog() = DialogResult.OK Then
                                                     AppUI.Toast($"Added {f.SupplierName}.", AppUI.ToastKind.Success)
                                                     LoadSuppliers()
                                                 End If
                                             End Using
                                         End Sub
        AddHandler btnEditSupplier.Click, Sub(s, e) EditSupplier()
        AddHandler btnExportSuppliers.Click, Sub(s, e) AppUI.ExportCsv(gridSuppliers.AllRows(), "suppliers", FindForm())
        AddHandler btnExportPOs.Click, Sub(s, e) AppUI.ExportCsv(gridPurchaseOrders.AllRows(), "purchase_orders", FindForm())
        AddHandler btnDeleteSupplier.Click, AddressOf btnDeleteSupplier_Click
        AddHandler btnNewPO.Click, AddressOf btnNewPO_Click
        AddHandler btnReceive.Click, AddressOf btnReceive_Click
        AddHandler btnMarkPaid.Click, AddressOf btnMarkPaid_Click
        AddHandler txtSearchSuppliers.TextChanged, Sub(s, e) LoadSuppliers()
        AddHandler txtSearchPOs.TextChanged, Sub(s, e) LoadPurchaseOrders()
        AddHandler Me.Load, Sub(s, e)
                                 If showPurchaseOrders Then LoadPurchaseOrders() Else LoadSuppliers()
                             End Sub
    End Sub

    Private Sub LoadSuppliers()
        Dim term = txtSearchSuppliers.Text.Trim()
        Dim p As New Dictionary(Of String, Object) From {{"@s", If(term = "", CObj(DBNull.Value), "%" & term & "%")}}
        gridSuppliers.Bind(DataAccess.GetTable(
            "SELECT s.SupplierID, s.Name, s.Category, s.ContactName, s.Phone, s.Email, s.[Address], s.TaxID, " &
            "s.Balance AS OwedAP " &
            "FROM Suppliers s " &
            "WHERE (@s IS NULL OR s.Name LIKE @s OR s.ContactName LIKE @s OR s.Phone LIKE @s OR s.Email LIKE @s OR s.Category LIKE @s OR s.TaxID LIKE @s) " &
            "ORDER BY s.Name", p), hiddenColumns:={"SupplierID"})
    End Sub

    Private Sub EditSupplier()
        If gridSuppliers.Grid.SelectedRows.Count = 0 Then Return
        Using f As New frmAddSupplier(CInt(gridSuppliers.Grid.SelectedRows(0).Cells("SupplierID").Value))
            If f.ShowDialog() = DialogResult.OK Then
                AppUI.Toast("Supplier updated.", AppUI.ToastKind.Success)
                LoadSuppliers()
            End If
        End Using
    End Sub

    Private Sub btnDeleteSupplier_Click(sender As Object, e As EventArgs)
        If gridSuppliers.Grid.SelectedRows.Count = 0 Then Return
        Dim row = gridSuppliers.Grid.SelectedRows(0)
        Dim supplierId = CInt(row.Cells("SupplierID").Value)
        Dim name = Convert.ToString(row.Cells("Name").Value)
        If AppUI.TryDelete(FindForm(), $"supplier ""{name}""",
                           "DELETE FROM Suppliers WHERE SupplierID = @id",
                           New Dictionary(Of String, Object) From {{"@id", supplierId}}) Then
            LoadSuppliers()
        End If
    End Sub

    Private Sub LoadPurchaseOrders()
        Dim sql = "SELECT po.POID, po.PONumber, s.Name AS Supplier, po.OrderDate, po.Status, po.PaymentStatus, po.TotalAmount, po.SupplierID " &
                  "FROM PurchaseOrders po JOIN Suppliers s ON s.SupplierID = po.SupplierID " &
                  "WHERE (@s IS NULL OR po.PONumber LIKE @s OR s.Name LIKE @s OR po.Status LIKE @s OR po.PaymentStatus LIKE @s) " &
                  "ORDER BY po.POID DESC"
        Dim term = txtSearchPOs.Text.Trim()
        Dim p As New Dictionary(Of String, Object) From {{"@s", If(term = "", CObj(DBNull.Value), "%" & term & "%")}}
        gridPurchaseOrders.Bind(DataAccess.GetTable(sql, p), hiddenColumns:={"POID", "SupplierID"})

        Dim ap = DataAccess.GetTable("SELECT ISNULL(SUM(Balance),0) AS Total FROM Suppliers").Rows(0)("Total")
        lblAP.Text = "Total owed to suppliers: " & AppInfo.Money(ap)
        UpdatePoButtons()
    End Sub

    Private Sub UpdatePoButtons()
        If gridPurchaseOrders.Grid.SelectedRows.Count = 0 Then
            btnReceive.Enabled = False
            btnMarkPaid.Enabled = False
            Return
        End If
        Dim row = gridPurchaseOrders.Grid.SelectedRows(0)
        btnReceive.Enabled = row.Cells("Status").Value.ToString() <> "Received"
        btnMarkPaid.Enabled = row.Cells("PaymentStatus").Value.ToString() <> "Paid"
    End Sub

    Private Sub btnNewPO_Click(sender As Object, e As EventArgs)
        Using f As New frmNewPO(currentUserId)
            If f.ShowDialog() = DialogResult.OK Then LoadPurchaseOrders()
        End Using
    End Sub

    ''' Bumps stock for each PO line into the target warehouse's batch
    ''' (creating the batch if it doesn't exist yet) and marks the PO Received.
    Private Sub btnReceive_Click(sender As Object, e As EventArgs)
        Dim poId = CInt(gridPurchaseOrders.Grid.SelectedRows(0).Cells("POID").Value)
        Dim items = DataAccess.GetTable("SELECT ProductID, Quantity FROM PurchaseOrderItems WHERE POID = @poId",
            New Dictionary(Of String, Object) From {{"@poId", poId}})
        Dim mainWarehouseId = Convert.ToInt32(DataAccess.GetTable("SELECT TOP 1 WarehouseID FROM Warehouses ORDER BY WarehouseID").Rows(0)("WarehouseID"))

        For Each row In items.AsEnumerable()
            Dim productId = CInt(row("ProductID"))
            Dim qty = CInt(row("Quantity"))

            ' Goods arriving from a supplier who ships them unlabelled get an
            ' in-store barcode as they're booked in, so they can be scanned at
            ' the till the moment they reach the shelf rather than waiting for
            ' someone to notice they have none.
            DataAccess.Execute(
                "UPDATE Products SET Barcode = @b WHERE ProductID = @p AND (Barcode IS NULL OR Barcode = '')",
                New Dictionary(Of String, Object) From {
                    {"@b", Barcodes.MintInternalBarcode(productId)}, {"@p", productId}})
            DataAccess.Execute(
                "IF EXISTS (SELECT 1 FROM StockBatches WHERE ProductID=@p AND WarehouseID=@w AND BatchNumber='PO-RECEIPT') " &
                "  UPDATE StockBatches SET QuantityOnHand = QuantityOnHand + @qty WHERE ProductID=@p AND WarehouseID=@w AND BatchNumber='PO-RECEIPT' " &
                "ELSE " &
                "  INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p, @w, 'PO-RECEIPT', @qty)",
                New Dictionary(Of String, Object) From {{"@p", productId}, {"@w", mainWarehouseId}, {"@qty", qty}})
        Next

        DataAccess.Execute("UPDATE PurchaseOrders SET Status = 'Received' WHERE POID = @poId",
            New Dictionary(Of String, Object) From {{"@poId", poId}})
        LoadPurchaseOrders()
    End Sub

    ''' Clears whatever is left owing on this one order — not its full total
    ''' again if part of it was already paid when it was created — and brings
    ''' the supplier's running Balance down by exactly that much.
    Private Sub btnMarkPaid_Click(sender As Object, e As EventArgs)
        Dim row = gridPurchaseOrders.Grid.SelectedRows(0)
        Dim poId = CInt(row.Cells("POID").Value)
        Dim supplierId = CInt(row.Cells("SupplierID").Value)
        Dim supplierName = row.Cells("Supplier").Value.ToString()
        Dim number = row.Cells("PONumber").Value.ToString()

        DataAccess.InTransaction(
            Function(conn, tx) As Boolean
                Dim r = DataAccess.TableIn(conn, tx, "SELECT TotalAmount, AmountPaid FROM PurchaseOrders WITH (UPDLOCK) WHERE POID = @id",
                                           New Dictionary(Of String, Object) From {{"@id", poId}})
                If r.Rows.Count = 0 Then Return False
                Dim outstanding = Convert.ToDecimal(r.Rows(0)("TotalAmount")) - Convert.ToDecimal(r.Rows(0)("AmountPaid"))
                If outstanding <= 0 Then Return True

                DataAccess.Exec(conn, tx, "UPDATE PurchaseOrders SET AmountPaid = TotalAmount, PaymentStatus = 'Paid' WHERE POID = @id",
                    New Dictionary(Of String, Object) From {{"@id", poId}})
                DataAccess.Exec(conn, tx, "UPDATE Suppliers SET Balance = Balance - @amt WHERE SupplierID = @sid",
                    New Dictionary(Of String, Object) From {{"@amt", outstanding}, {"@sid", supplierId}})
                DataAccess.Exec(conn, tx,
                    "INSERT INTO Ledger (AccountType, AccountName, EntryType, Amount, Reference) VALUES ('Supplier', @name, 'Debit', @amount, @ref)",
                    New Dictionary(Of String, Object) From {{"@name", supplierName}, {"@amount", outstanding}, {"@ref", number}})
                Return True
            End Function)
        LoadPurchaseOrders()
    End Sub

End Class
