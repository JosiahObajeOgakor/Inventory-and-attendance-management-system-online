Imports System.Windows.Forms
Imports System.Data

''' Quotation list. A quotation is a price computation for a customer — it
''' never touches stock, the ledger or a customer's balance. It sits here
''' either until it's deleted (nothing to undo — nothing ever moved) or
''' converted into a real sale via "Convert to sale", which opens frmNewInvoice
''' pre-filled from it and only then runs the ordinary Sales.Save transaction.
Public Class ucQuotations
    Inherits UserControl

    Private ReadOnly currentUserId As Integer
    Private ReadOnly isAdmin As Boolean
    Private ReadOnly grid As New PagedGrid() With {.PageSize = 5}
    Private txtSearch As New TextBox() With {.Width = 200}
    Private btnNewQuotation As New Button() With {.Text = "+ New quotation", .Tag = "primary", .AutoSize = True}
    Private btnConvert As New Button() With {.Text = "Convert to sale", .AutoSize = True, .Enabled = False}
    Private btnView As New Button() With {.Text = "View / PDF", .AutoSize = True, .Enabled = False}
    Private btnDelete As New Button() With {.Text = "Delete quotation", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private btnPriceChanges As New Button() With {.Text = "Price changes", .AutoSize = True}
    Private toolbar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(12)}

    Public Sub New(userId As Integer, isAdminUser As Boolean)
        currentUserId = userId
        isAdmin = isAdminUser
        toolbar.Controls.Add(btnNewQuotation)
        toolbar.Controls.Add(btnConvert)
        toolbar.Controls.Add(btnView)
        If isAdmin Then
            toolbar.Controls.Add(btnDelete)
            toolbar.Controls.Add(btnPriceChanges)
        End If
        toolbar.Controls.Add(txtSearch)
        toolbar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(6, 8, 6, 0)})
        Controls.Add(grid)
        Controls.Add(toolbar)
        AddHandler btnNewQuotation.Click, AddressOf btnNewQuotation_Click
        AddHandler btnConvert.Click, AddressOf btnConvert_Click
        AddHandler btnView.Click, Sub(s, e) OpenView()
        AddHandler btnDelete.Click, AddressOf btnDelete_Click
        AddHandler btnPriceChanges.Click, Sub(s, e) ShowPriceChanges()
        AddHandler txtSearch.TextChanged, Sub(s, e) LoadGrid()
        AddHandler grid.Grid.SelectionChanged, Sub(s, e)
                                                    Dim has = grid.Grid.SelectedRows.Count > 0
                                                    Dim isOpen = has AndAlso Convert.ToString(grid.Grid.SelectedRows(0).Cells("Status").Value) = "Open"
                                                    btnConvert.Enabled = isOpen
                                                    btnView.Enabled = has
                                                    btnDelete.Enabled = has
                                                End Sub
        AddHandler grid.Grid.CellDoubleClick, Sub(s, e) OpenView()
        AddHandler Me.Load, Sub(s, e) LoadGrid()
    End Sub

    Private Sub LoadGrid()
        Dim sql =
            "SELECT q.QuotationNumber, c.Name AS Customer, q.QuotationDate, q.TotalAmount, q.Status, " &
            "CASE WHEN ro.RoleName = 'Warehouse Clerk' THEN NULL ELSE u.FullName END AS PreparedByName, " &
            "ro.RoleName AS PreparedByRole, q.QuotationID " &
            "FROM Quotations q JOIN Customers c ON c.CustomerID = q.CustomerID " &
            "LEFT JOIN Users u ON u.UserID = q.CreatedByUserID " &
            "LEFT JOIN Roles ro ON ro.RoleID = u.RoleID " &
            "WHERE (@s IS NULL OR q.QuotationNumber LIKE @s OR c.Name LIKE @s OR q.Status LIKE @s) " &
            "ORDER BY q.QuotationID DESC"
        Dim term = txtSearch.Text.Trim()
        Dim p As New Dictionary(Of String, Object) From {{"@s", If(term = "", CObj(DBNull.Value), "%" & term & "%")}}
        Dim t = DataAccess.GetTable(sql, p)
        t.Columns.Add("Prepared by", GetType(String))
        For Each r As DataRow In t.Rows
            r("Prepared by") = If(Convert.ToString(r("PreparedByRole")) = "Warehouse Clerk",
                                   $"{AppInfo.CompanyName} (Clerk)", Convert.ToString(r("PreparedByName")))
        Next
        grid.Bind(t, hiddenColumns:={"QuotationID", "PreparedByName", "PreparedByRole"})
    End Sub

    Private Function SelectedQuotationId() As Integer?
        If grid.Grid.SelectedRows.Count = 0 Then Return Nothing
        Return Convert.ToInt32(grid.Grid.SelectedRows(0).Cells("QuotationID").Value)
    End Function

    Private Sub btnNewQuotation_Click(sender As Object, e As EventArgs)
        Using f As New frmNewQuotation(currentUserId)
            If f.ShowDialog() = DialogResult.OK Then LoadGrid()
        End Using
    End Sub

    ''' Opens a real sale pre-filled from this quotation — still fully
    ''' editable there (add, remove, replace, reprice) before anything is
    ''' saved. Only marks the quotation Converted once the sale actually
    ''' saves; closing without saving leaves the quotation untouched.
    Private Sub btnConvert_Click(sender As Object, e As EventArgs)
        Dim id = SelectedQuotationId()
        If Not id.HasValue Then Return
        Using f As New frmNewInvoice(currentUserId, id.Value)
            If f.ShowDialog() = DialogResult.OK Then
                Quotations.ConvertToSale(id.Value, f.SavedInvoiceId)
                AppUI.Toast("Quotation converted to a sale.", AppUI.ToastKind.Success)
                LoadGrid()
            End If
        End Using
    End Sub

    Private Sub OpenView()
        Dim id = SelectedQuotationId()
        If Not id.HasValue Then Return
        Try
            BuildQuotationDoc(id.Value).ShowPreview(FindForm())
        Catch ex As Exception
            AppUI.Toast("Could not open quotation: " & ex.Message, AppUI.ToastKind.Error)
        End Try
    End Sub

    Private Sub btnDelete_Click(sender As Object, e As EventArgs)
        Dim id = SelectedQuotationId()
        If Not id.HasValue Then Return
        Dim number = Convert.ToString(grid.Grid.SelectedRows(0).Cells("QuotationNumber").Value)
        If Not AppUI.Confirm(FindForm(),
            $"Delete quotation {number}?" & vbCrLf & vbCrLf &
            "Nothing on it was ever taken from stock or charged to the customer, so nothing needs undoing." & vbCrLf & vbCrLf &
            "This cannot be undone.", "Delete quotation", "Delete", danger:=True) Then Return
        Try
            Quotations.Delete(id.Value)
            AppUI.Toast($"Quotation {number} deleted.", AppUI.ToastKind.Success)
            LoadGrid()
        Catch ex As Exception
            AppUI.Toast("Delete failed: " & ex.Message, AppUI.ToastKind.Error)
        End Try
    End Sub

    ''' Read-only history of every manual price override, on a sale or a
    ''' quotation — who changed what, away from the standard tier price.
    Private Sub ShowPriceChanges()
        Using f As New Form() With {.Text = "Price changes", .Width = 900, .Height = 560, .StartPosition = FormStartPosition.CenterParent}
            Dim g As New PagedGrid() With {.PageSize = 12, .Dock = DockStyle.Fill}
            g.Bind(PriceOverrides.History())
            f.Controls.Add(g)
            Theme.Apply(f)
            f.ShowDialog(FindForm())
        End Using
    End Sub

    ' ===== printing =====

    Friend Shared Function BuildQuotationDoc(quotationId As Integer) As DocPrinter
        Dim header = DataAccess.GetTable(
            "SELECT q.QuotationNumber, q.QuotationDate, q.Subtotal, q.DiscountPct, q.DiscountAmount, " &
            "q.VATRate, q.VATAmount, q.TotalAmount, q.PriceTier, q.[Status], " &
            "c.Name AS CustomerName, c.ContactName, ISNULL(c.Phone,'') AS CustomerPhone, ISNULL(c.Email,'') AS CustomerEmail, " &
            "ISNULL(c.[Address],'') AS CustomerAddress, ISNULL(c.Location,'') AS CustomerLocation, c.CustomerType, " &
            "ISNULL(u.FullName,'') AS CreatedBy, ISNULL(ro.RoleName,'') AS CreatedByRole " &
            "FROM Quotations q JOIN Customers c ON c.CustomerID = q.CustomerID " &
            "LEFT JOIN Users u ON u.UserID = q.CreatedByUserID " &
            "LEFT JOIN Roles ro ON ro.RoleID = u.RoleID WHERE q.QuotationID = @id",
            New Dictionary(Of String, Object) From {{"@id", quotationId}}).Rows(0)

        Dim lines = DataAccess.GetTable(
            "SELECT p.Name AS Item, qi.Quantity AS Qty, qi.UnitPrice AS Price, qi.LineTotal AS [Line total] " &
            "FROM QuotationItems qi JOIN Products p ON p.ProductID = qi.ProductID WHERE qi.QuotationID = @id ORDER BY p.Name",
            New Dictionary(Of String, Object) From {{"@id", quotationId}})

        Dim number = Convert.ToString(header("QuotationNumber"))
        Dim s = Function(col As String) Convert.ToString(header(col))
        Dim preparedBy = If(s("CreatedByRole") = "Warehouse Clerk", $"{AppInfo.CompanyName} (Clerk)", s("CreatedBy"))

        Dim d As New DocPrinter() With {
            .DocTitle = "Quotation " & number, .FitToOnePage = True, .Watermark = True,
            .FooterText = $"{AppInfo.CompanyName}   ·   Quotation {number}   ·   computer-generated {DateTime.Now:dd MMM yyyy HH:mm}"}

        d.Letterhead("QUOTATION", {
            ("Quotation no.", number),
            ("Date", Convert.ToDateTime(header("QuotationDate")).ToString("dd MMM yyyy")),
            ("Status", s("Status").ToUpperInvariant())})

        d.Panels(
            ("Quoted to", {
                ("Customer", s("CustomerName"), True),
                ("Contact", s("ContactName"), False),
                ("Address", String.Join(", ", {s("CustomerAddress"), s("CustomerLocation")}.Where(Function(x) x <> "")), False),
                ("Phone", s("CustomerPhone"), False),
                ("Email", s("CustomerEmail"), False),
                ("Ranking", s("CustomerType"), False)}),
            ("Quote details", {
                ("Price tier", s("PriceTier"), False),
                ("Prepared by", preparedBy, False)}))

        Dim items As New DataTable()
        For Each c In {"#", "Description", "Qty", $"Unit price ({AppInfo.CurrencySymbol})", $"Amount ({AppInfo.CurrencySymbol})"}
            items.Columns.Add(c)
        Next
        Dim n = 0
        For Each r As DataRow In lines.Rows
            n += 1
            items.Rows.Add(n, r("Item"), Convert.ToInt32(r("Qty")).ToString("#,0"),
                           Convert.ToDecimal(r("Price")).ToString("N2"), Convert.ToDecimal(r("Line total")).ToString("N2"))
        Next
        d.Table(items, {0.45F, 4.4F, 0.9F, 1.6F, 1.7F}, rightAlignFrom:=2)

        Dim totals As New List(Of (K As String, V As String, Style As Integer)) From {
            ("Subtotal", AppInfo.Money2(header("Subtotal")), 0)}
        If Convert.ToDecimal(header("DiscountAmount")) > 0 Then
            totals.Add(($"Discount ({Convert.ToDecimal(header("DiscountPct")):0.##}%)", "−" & AppInfo.Money2(header("DiscountAmount")), 0))
        End If
        If Convert.ToDecimal(header("VATAmount")) > 0 Then
            totals.Add(($"VAT ({Convert.ToDecimal(header("VATRate")):0.##}%)", AppInfo.Money2(header("VATAmount")), 0))
        End If
        totals.Add(("TOTAL (quoted)", AppInfo.Money2(header("TotalAmount")), 1))
        d.Totals(totals, {
            ($"{n} product line(s)", False),
            ("This is a price quotation, not an invoice — no stock has been reserved " &
             "and nothing is owed until it's converted to a sale.", True)})

        d.Gap(4)
        ' Every customer document carries its own scannable code, exactly like
        ' the sales receipt and the waybill — so this quotation can be pulled
        ' straight back up at the counter instead of hunted for by number.
        Try
            d.Barcode(Barcodes.Code128(number, heightPx:=52, moduleWidth:=2, showText:=False),
                      "Scan to look this quotation up  ·  " & number)
        Catch
            ' A symbol is a convenience; never lose the quotation over one.
        End Try

        d.BrandFooter("Thanks for your interest — let us know if you'd like to go ahead.")
        Return d
    End Function

End Class
