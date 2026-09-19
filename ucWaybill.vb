Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data

''' Waybills, as two steps rather than three tables fighting for one window:
'''
'''   • "Sales to deliver" — the sales list, with the goods on the selected sale
'''     underneath it, and the button that turns that sale into a waybill.
'''   • "Waybills issued" — what's already been issued, with the goods each one
'''     covers underneath, and reprint.
'''
''' Each table gets the full width and pages five at a time, so a busy week
''' doesn't turn the screen into one long scroll.
Public Class ucWaybill
    Inherits UserControl

    Private Const RowsPerTable As Integer = 5

    Private ReadOnly currentUserId As Integer
    Private ReadOnly pgSales As New PagedGrid() With {.PageSize = RowsPerTable}
    Private ReadOnly pgSaleItems As New PagedGrid() With {.PageSize = RowsPerTable}
    Private ReadOnly pgWaybills As New PagedGrid() With {.PageSize = RowsPerTable}
    Private ReadOnly pgWaybillItems As New PagedGrid() With {.PageSize = RowsPerTable}
    Private lblSaleItems As New Label() With {.AutoSize = True, .Tag = "keepfont", .Padding = New Padding(8, 4, 8, 6), .Dock = DockStyle.Top}
    Private lblWaybillItems As New Label() With {.AutoSize = True, .Tag = "keepfont", .Padding = New Padding(8, 4, 8, 6), .Dock = DockStyle.Top}
    Private chkPending As New CheckBox() With {.Text = "Only sales without a waybill", .AutoSize = True, .Margin = New Padding(12, 8, 0, 0)}
    Private btnCreate As New Button() With {.Text = "Create waybill for selected sale", .Tag = "primary", .AutoSize = True, .Enabled = False}
    Private btnPrint As New Button() With {.Text = "Print / PDF waybill", .AutoSize = True, .Enabled = False}
    Private btnExport As New Button() With {.Text = "Export CSV", .AutoSize = True}
    Private txtSearchSales As New TextBox() With {.Width = 160}
    Private txtSearchWaybills As New TextBox() With {.Width = 160}

    Public Sub New(userId As Integer)
        currentUserId = userId

        Dim tabs As New TabControl() With {.Dock = DockStyle.Fill}
        tabs.TabPages.Add(BuildSalesTab())
        tabs.TabPages.Add(BuildWaybillsTab())
        Controls.Add(tabs)

        AddHandler pgSales.Grid.SelectionChanged, Sub(s, e)
                                                      btnCreate.Enabled = pgSales.Grid.SelectedRows.Count > 0
                                                      ShowItemsForSelectedSale()
                                                  End Sub
        AddHandler pgWaybills.Grid.SelectionChanged, Sub(s, e)
                                                         btnPrint.Enabled = pgWaybills.Grid.SelectedRows.Count > 0
                                                         ShowItemsForSelectedWaybill()
                                                     End Sub
        AddHandler pgWaybills.Grid.CellDoubleClick, Sub(s, e) PrintSelected()
        AddHandler chkPending.CheckedChanged, Sub(s, e) LoadSales()
        AddHandler txtSearchSales.TextChanged, Sub(s, e) pgSales.Search(txtSearchSales.Text)
        AddHandler txtSearchWaybills.TextChanged, Sub(s, e) pgWaybills.Search(txtSearchWaybills.Text)
        AddHandler btnCreate.Click, AddressOf Create_Click
        AddHandler btnPrint.Click, Sub(s, e) PrintSelected()
        AddHandler btnExport.Click, Sub(s, e) AppUI.ExportCsv(pgWaybills.AllRows(), "waybills", FindForm())
        AddHandler Me.Load, Sub(s, e) LoadAll()
    End Sub

    ''' A list on top, the detail it drives underneath — the shape both tabs use.
    Private Shared Function TwoRowLayout(topPanel As Control, bottomPanel As Control) As TableLayoutPanel
        Dim layout As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .RowCount = 2, .ColumnCount = 1}
        layout.RowStyles.Add(New RowStyle(SizeType.Percent, 58))
        layout.RowStyles.Add(New RowStyle(SizeType.Percent, 42))
        layout.Controls.Add(topPanel, 0, 0)
        layout.Controls.Add(bottomPanel, 0, 1)
        Return layout
    End Function

    Private Function BuildSalesTab() As TabPage
        Dim tp As New TabPage("Sales to deliver")

        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8)}
        bar.Controls.Add(btnCreate)
        bar.Controls.Add(chkPending)
        bar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(16, 8, 6, 0)})
        bar.Controls.Add(txtSearchSales)
        Dim listPanel As New Panel() With {.Dock = DockStyle.Fill}
        listPanel.Controls.Add(pgSales)
        listPanel.Controls.Add(bar)

        Dim itemsPanel As New Panel() With {.Dock = DockStyle.Fill}
        itemsPanel.Controls.Add(pgSaleItems)
        itemsPanel.Controls.Add(lblSaleItems)
        itemsPanel.Controls.Add(New Label() With {.Text = "Goods on the selected sale", .Dock = DockStyle.Top,
                                                  .Tag = "heading", .Padding = New Padding(8), .AutoSize = True})

        tp.Controls.Add(TwoRowLayout(listPanel, itemsPanel))
        Return tp
    End Function

    Private Function BuildWaybillsTab() As TabPage
        Dim tp As New TabPage("Waybills issued")

        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8)}
        bar.Controls.Add(btnPrint)
        bar.Controls.Add(btnExport)
        bar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(16, 8, 6, 0)})
        bar.Controls.Add(txtSearchWaybills)
        Dim listPanel As New Panel() With {.Dock = DockStyle.Fill}
        listPanel.Controls.Add(pgWaybills)
        listPanel.Controls.Add(bar)

        Dim itemsPanel As New Panel() With {.Dock = DockStyle.Fill}
        itemsPanel.Controls.Add(pgWaybillItems)
        itemsPanel.Controls.Add(lblWaybillItems)
        itemsPanel.Controls.Add(New Label() With {.Text = "Goods on the selected waybill", .Dock = DockStyle.Top,
                                                  .Tag = "heading", .Padding = New Padding(8), .AutoSize = True})

        tp.Controls.Add(TwoRowLayout(listPanel, itemsPanel))
        Return tp
    End Function

    Private Sub LoadAll()
        LoadSales()
        LoadWaybills()
    End Sub

    Private Sub LoadWaybills()
        pgWaybills.Bind(
            "SELECT wb.WaybillID, wb.InvoiceID, wb.WaybillNumber AS [Waybill], wb.IssueDate AS [Date], i.InvoiceNumber AS [Invoice], c.Name AS Customer, " &
            "(SELECT COUNT(*) FROM InvoiceItems ii WHERE ii.InvoiceID=wb.InvoiceID) AS Lines, " &
            "(SELECT ISNULL(SUM(ii.Quantity),0) FROM InvoiceItems ii WHERE ii.InvoiceID=wb.InvoiceID) AS [Total qty], " &
            "wb.DriverName AS Driver, wb.DriverPhone AS [Driver phone], wb.VehiclePlate AS [Plate], wb.DestinationAddress AS Destination " &
            "FROM Waybills wb JOIN Invoices i ON i.InvoiceID=wb.InvoiceID JOIN Customers c ON c.CustomerID=i.CustomerID",
            "wb.WaybillID DESC",
            hiddenColumns:={"WaybillID", "InvoiceID"},
            searchColumns:={"Waybill", "Customer", "Driver", "Driver phone", "Plate", "Invoice"})
        ShowItemsForSelectedWaybill()
    End Sub

    Private Sub LoadSales()
        pgSales.Bind(
            "SELECT i.InvoiceID, i.InvoiceNumber AS [Invoice], i.InvoiceDate AS [Date], c.Name AS Customer, " &
            "ISNULL(w.Name,'') AS Warehouse, " &
            "(SELECT COUNT(*) FROM InvoiceItems ii WHERE ii.InvoiceID=i.InvoiceID) AS Lines, " &
            "(SELECT ISNULL(SUM(ii.Quantity),0) FROM InvoiceItems ii WHERE ii.InvoiceID=i.InvoiceID) AS [Total qty], " &
            "i.TotalAmount AS Amount, i.[Status], " &
            "(SELECT COUNT(*) FROM Waybills wb WHERE wb.InvoiceID=i.InvoiceID) AS Waybills " &
            "FROM Invoices i JOIN Customers c ON c.CustomerID=i.CustomerID LEFT JOIN Warehouses w ON w.WarehouseID=i.WarehouseID " &
            "WHERE (@pending = 0 OR NOT EXISTS (SELECT 1 FROM Waybills wb WHERE wb.InvoiceID=i.InvoiceID))",
            "i.InvoiceID DESC",
            New Dictionary(Of String, Object) From {{"@pending", If(chkPending.Checked, 1, 0)}},
            hiddenColumns:={"InvoiceID"},
            searchColumns:={"Invoice", "Customer"})
        ShowItemsForSelectedSale()
    End Sub

    Private Sub ShowItemsForSelectedSale()
        If pgSales.Grid.SelectedRows.Count = 0 Then
            pgSaleItems.Bind(New DataTable())
            lblSaleItems.Text = "Select a sale to see its products."
            Return
        End If
        Dim invoiceId = CInt(pgSales.Grid.SelectedRows(0).Cells("InvoiceID").Value)
        pgSaleItems.Bind(InvoiceItems(invoiceId))
        lblSaleItems.Text = ItemSummary(invoiceId, Nothing)
    End Sub

    Private Sub ShowItemsForSelectedWaybill()
        If pgWaybills.Grid.SelectedRows.Count = 0 Then
            pgWaybillItems.Bind(New DataTable())
            lblWaybillItems.Text = "Select a waybill to see the goods it covers."
            Return
        End If
        Dim r = pgWaybills.Grid.SelectedRows(0)
        Dim invoiceId = CInt(r.Cells("InvoiceID").Value)
        pgWaybillItems.Bind(InvoiceItems(invoiceId))
        lblWaybillItems.Text = ItemSummary(invoiceId, Convert.ToString(r.Cells("Waybill").Value))
    End Sub

    ''' Who it's for and how much of it — the line above the goods table.
    Private Function ItemSummary(invoiceId As Integer, waybillNumber As String) As String
        Dim items = InvoiceItems(invoiceId)
        Dim h = DataAccess.GetTable(
            "SELECT i.InvoiceNumber, c.Name, ISNULL(c.Phone,'') AS Phone, ISNULL(w.Name,'') AS Warehouse " &
            "FROM Invoices i JOIN Customers c ON c.CustomerID=i.CustomerID LEFT JOIN Warehouses w ON w.WarehouseID=i.WarehouseID WHERE i.InvoiceID=@id",
            New Dictionary(Of String, Object) From {{"@id", invoiceId}}).Rows(0)
        Dim qty = items.AsEnumerable().Sum(Function(x) Convert.ToInt32(x("Qty")))
        Return If(waybillNumber Is Nothing, "", $"Waybill {waybillNumber}  ·  ") &
               $"Invoice {h("InvoiceNumber")}  ·  {h("Name")}{If(Convert.ToString(h("Phone")) = "", "", "  ·  " & Convert.ToString(h("Phone")))}" & vbCrLf &
               $"From {If(Convert.ToString(h("Warehouse")) = "", "—", Convert.ToString(h("Warehouse")))}  ·  {items.Rows.Count} product line(s), {qty:#,0} unit(s) in total"
    End Function

    Friend Shared Function InvoiceItems(invoiceId As Integer) As DataTable
        Return DataAccess.GetTable(
            "SELECT p.SKU, p.Name AS Product, ISNULL(cat.Name,'') AS Category, ii.Quantity AS Qty, p.Unit, " &
            "ii.UnitPrice AS [Unit price], ii.LineTotal AS [Line total] " &
            "FROM InvoiceItems ii JOIN Products p ON p.ProductID=ii.ProductID " &
            "LEFT JOIN Categories cat ON cat.CategoryID=p.CategoryID WHERE ii.InvoiceID=@id ORDER BY p.Name",
            New Dictionary(Of String, Object) From {{"@id", invoiceId}})
    End Function

    Private Sub Create_Click(sender As Object, e As EventArgs)
        If pgSales.Grid.SelectedRows.Count = 0 Then Return
        Dim invoiceId = CInt(pgSales.Grid.SelectedRows(0).Cells("InvoiceID").Value)
        Using f As New frmNewWaybill(invoiceId, currentUserId)
            If f.ShowDialog(FindForm()) = DialogResult.OK Then
                LoadAll()
                PrintWaybill(f.NewWaybillId)
            End If
        End Using
    End Sub

    Private Sub PrintSelected()
        If pgWaybills.Grid.SelectedRows.Count = 0 Then Return
        PrintWaybill(CInt(pgWaybills.Grid.SelectedRows(0).Cells("WaybillID").Value))
    End Sub

    Private Sub PrintWaybill(waybillId As Integer)
        Dim h = DataAccess.GetTable(
            "SELECT wb.WaybillNumber, wb.IssueDate, wb.DriverName, wb.DriverPhone, wb.VehiclePlate, " &
            "wb.DestinationAddress, wb.Notes, wb.InvoiceID, i.InvoiceNumber, i.InvoiceDate, c.Name AS Customer, ISNULL(c.Phone,'') AS Phone, " &
            "ISNULL(c.ContactName,'') AS Contact, ISNULL(w.Name,'') AS Warehouse, ISNULL(w.Location,'') AS WarehouseLocation, " &
            "ISNULL(u.FullName,'') AS IssuedBy, ISNULL(ro.RoleName,'') AS IssuedByRole " &
            "FROM Waybills wb JOIN Invoices i ON i.InvoiceID=wb.InvoiceID JOIN Customers c ON c.CustomerID=i.CustomerID " &
            "LEFT JOIN Warehouses w ON w.WarehouseID=i.WarehouseID LEFT JOIN Users u ON u.UserID=wb.CreatedByUserID " &
            "LEFT JOIN Roles ro ON ro.RoleID=u.RoleID WHERE wb.WaybillID=@id",
            New Dictionary(Of String, Object) From {{"@id", waybillId}}).Rows(0)

        BuildWaybillDoc(h).ShowPreview(FindForm())
    End Sub

    Friend Shared Function BuildWaybillDoc(h As DataRow) As DocPrinter
        Dim s = Function(col As String) Convert.ToString(h(col))
        Dim all = InvoiceItems(CInt(h("InvoiceID")))
        Dim goods As New DataTable()
        For Each c In {"#", "SKU", "Description", "Unit", "Qty", "Received"}
            goods.Columns.Add(c)
        Next
        Dim n = 0, totalQty = 0
        For Each r As DataRow In all.Rows
            n += 1
            totalQty += Convert.ToInt32(r("Qty"))
            goods.Rows.Add(n, r("SKU"), r("Product"), r("Unit"), Convert.ToInt32(r("Qty")).ToString("#,0"), "")
        Next

        Dim number = s("WaybillNumber")
        ' Always exactly one A4 page — scales down to fit, nothing is cut.
        Dim d As New DocPrinter() With {
            .DocTitle = "Waybill " & number, .FitToOnePage = True, .Watermark = True,
            .FooterText = $"{AppInfo.CompanyName}   ·   Waybill {number}   ·   computer-generated {DateTime.Now:dd MMM yyyy HH:mm}"}

        ' A clerk login is shared front-desk staff, not one named person, and its
        ' stored FullName can carry the other business's branding (it's mirrored
        ' between companies). So a clerk-issued waybill shows "<company> (Clerk)"
        ' instead of that stored name; only an Admin issue prints the real name.
        Dim issuedBy = If(s("IssuedByRole") = "Warehouse Clerk", $"{AppInfo.CompanyName} (Clerk)", s("IssuedBy"))

        d.Letterhead("WAYBILL / DELIVERY NOTE", {
            ("Waybill no.", number),
            ("Date issued", Convert.ToDateTime(h("IssueDate")).ToString("dd MMM yyyy")),
            ("Invoice no.", s("InvoiceNumber")),
            ("Invoice date", Convert.ToDateTime(h("InvoiceDate")).ToString("dd MMM yyyy"))})

        d.Panels(
            ("Deliver to", {
                ("Customer", s("Customer"), True),
                ("Contact", s("Contact"), False),
                ("Phone", s("Phone"), False),
                ("Address", s("DestinationAddress"), False)}),
            ("Dispatch", {
                ("From", String.Join(", ", {s("Warehouse"), s("WarehouseLocation")}.Where(Function(x) x <> "")), True),
                ("Issued by", issuedBy, False),
                ("Lines", n.ToString(), False),
                ("Total units", totalQty.ToString("#,0"), False)}),
            ("Carrier", {
                ("Driver", s("DriverName"), True),
                ("Phone", s("DriverPhone"), False),
                ("Vehicle", s("VehiclePlate"), True)}))

        d.SectionTitle("Goods dispatched")
        d.Table(goods, {0.45F, 1.3F, 3.8F, 0.9F, 0.9F, 1.1F}, rightAlignFrom:=4,
                footer:={"", "", $"Total — {n} line(s)", "", totalQty.ToString("#,0"), ""})

        If s("Notes") <> "" Then
            d.SectionTitle("Notes")
            d.Text(s("Notes"), size:=9)
        End If
        ' Every waybill carries a scannable code of its own, exactly like the
        ' sales receipt — both businesses, not just Candid Purrfect.
        Try
            d.Gap(6)
            d.Barcode(Barcodes.Code128(number, heightPx:=52, moduleWidth:=2, showText:=False),
                      "Scan to look this delivery up  ·  " & number)
        Catch
            ' A symbol is a convenience; never lose the waybill over one.
        End Try
        d.Gap(14)
        ' "Dispatched by" is stamped by the business itself (its own "confirmed
        ' and released" mark) rather than left as a blank signature/date for
        ' someone to fill in by hand; driver and the receiving customer still
        ' sign for themselves.
        d.DispatchSignatures(Company.Current.WaybillStamp, "Driver", "Received by (customer)")
        d.Text("Received the goods listed above in good condition and complete.", size:=8, grey:=True)
        d.BrandFooter("Thanks for your patronage.")
        Return d
    End Function

End Class
