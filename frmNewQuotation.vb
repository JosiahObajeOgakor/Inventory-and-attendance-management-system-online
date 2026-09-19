Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data

''' A price computation for a customer — same line-item/pricing experience as
''' a sale, but never touches stock, the ledger or a customer's balance. Left
''' idle or deleted freely; only converting it (ucQuotations' "Convert to
''' sale", which opens frmNewInvoice pre-filled from this) ever moves stock.
Public Class frmNewQuotation
    Inherits Form

    Private ReadOnly currentUserId As Integer
    Private cboCustomer As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 240}
    Private btnNewCustomer As New Button() With {.Text = "+ New", .AutoSize = True}
    Private cboTier As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 140}
    Private cboProduct As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 260}
    Private numQty As New NumericUpDown() With {.Minimum = 1, .Maximum = 1000000, .Value = 1}
    Private lblUnit As New Label() With {.AutoSize = True, .Tag = "keepfont", .Margin = New Padding(8, 8, 0, 0)}
    Private btnAddLine As New Button() With {.Text = "Add line", .Tag = "primary", .AutoSize = True}
    Private gridLines As DataGridView = UiHelpers.NewGrid()
    Private btnRemoveLine As New Button() With {.Text = "Remove line", .AutoSize = True, .Enabled = False}
    Private btnEditPrice As New Button() With {.Text = "Edit price", .AutoSize = True, .Enabled = False}
    Private numDiscountPct As New NumericUpDown() With {.Maximum = 100, .DecimalPlaces = 1}
    Private lblSubtotal As New Label() With {.AutoSize = True, .Tag = "keepfont"}
    Private lblDiscount As New Label() With {.AutoSize = True, .Tag = "keepfont"}
    Private lblVat As New Label() With {.AutoSize = True, .Tag = "keepfont"}
    Private chkVat As New CheckBox() With {.AutoSize = True, .Checked = False}
    Private lblTotal As New Label() With {.AutoSize = True, .Tag = "keepfont", .Font = New Font("Segoe UI", 13, FontStyle.Bold)}

    Private lineItems As New DataTable()
    Private prices As DataTable
    Private ReadOnly ConfiguredVatRate As Decimal = AppInfo.VatRate

    Public ReadOnly Property SavedQuotationId As Integer

    Private ReadOnly Property VatRate As Decimal
        Get
            Return If(chkVat.Checked, ConfiguredVatRate, 0D)
        End Get
    End Property

    Public Sub New(userId As Integer)
        currentUserId = userId
        Text = "New quotation"
        Width = 780
        Height = 640
        StartPosition = FormStartPosition.CenterParent

        ReloadCustomers()
        cboTier.Items.AddRange({"Distributor", "Wholesaler", "Retailer", "Walk-in"})
        cboTier.SelectedIndex = 2

        prices = DataAccess.GetTable("SELECT ProductID, Name, PriceDistributor, PriceWholesaler, PriceRetail FROM Products WHERE IsActive = 1 ORDER BY Name")
        cboProduct.DataSource = prices
        cboProduct.DisplayMember = "Name"
        cboProduct.ValueMember = "ProductID"

        lineItems.Columns.Add("ProductID", GetType(Integer))
        lineItems.Columns.Add("Product", GetType(String))
        lineItems.Columns.Add("Qty", GetType(Integer))
        lineItems.Columns.Add("UnitPrice", GetType(Decimal))
        lineItems.Columns.Add("LineTotal", GetType(Decimal))
        gridLines.DataSource = lineItems

        BuildLayout()

        AddHandler cboTier.SelectedIndexChanged, Sub(s, e) RepriceLines()
        AddHandler btnNewCustomer.Click, AddressOf btnNewCustomer_Click
        AddHandler btnAddLine.Click, AddressOf btnAddLine_Click
        AddHandler btnRemoveLine.Click, Sub(s, e)
                                            If gridLines.CurrentRow IsNot Nothing AndAlso gridLines.CurrentRow.Index < lineItems.Rows.Count Then
                                                lineItems.Rows.RemoveAt(gridLines.CurrentRow.Index)
                                                RecalculateTotals()
                                            End If
                                        End Sub
        AddHandler btnEditPrice.Click, AddressOf btnEditPrice_Click
        AddHandler gridLines.SelectionChanged, Sub(s, e)
                                                    Dim has = gridLines.SelectedRows.Count > 0
                                                    btnRemoveLine.Enabled = has
                                                    btnEditPrice.Enabled = has
                                                End Sub
        AddHandler numDiscountPct.ValueChanged, Sub(s, e) RecalculateTotals()
        AddHandler chkVat.CheckedChanged, Sub(s, e) RecalculateTotals()

        RecalculateTotals()
        UiHelpers.FitToScreen(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub ReloadCustomers()
        Dim t = DataAccess.GetTable("SELECT CustomerID, Name, CustomerType FROM Customers ORDER BY Name")
        cboCustomer.DataSource = t
        cboCustomer.DisplayMember = "Name"
        cboCustomer.ValueMember = "CustomerID"
    End Sub

    Private Function TierPrice(productId As Integer) As Decimal
        Dim r = prices.Select("ProductID = " & productId)
        If r.Length = 0 Then Return 0
        Select Case cboTier.Text
            Case "Distributor" : Return Convert.ToDecimal(r(0)("PriceDistributor"))
            Case "Wholesaler" : Return Convert.ToDecimal(r(0)("PriceWholesaler"))
            Case Else : Return Convert.ToDecimal(r(0)("PriceRetail"))
        End Select
    End Function

    Private Sub RepriceLines()
        For Each r As DataRow In lineItems.Rows
            Dim price = TierPrice(CInt(r("ProductID")))
            r("UnitPrice") = price
            r("LineTotal") = price * CInt(r("Qty"))
        Next
        RecalculateTotals()
    End Sub

    Private Sub BuildLayout()
        Dim top As New TableLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 2, .Padding = New Padding(16, 12, 16, 4)}
        top.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 120))

        Dim custRow As New FlowLayoutPanel() With {.AutoSize = True}
        custRow.Controls.Add(cboCustomer)
        custRow.Controls.Add(btnNewCustomer)
        AddRow(top, "Customer", custRow)
        AddRow(top, "Price tier", cboTier)

        Dim lineRow As New FlowLayoutPanel() With {.AutoSize = True}
        lineRow.Controls.Add(cboProduct)
        lineRow.Controls.Add(New Label() With {.Text = "Qty:", .AutoSize = True, .Margin = New Padding(10, 8, 4, 0)})
        lineRow.Controls.Add(numQty)
        lineRow.Controls.Add(lblUnit)
        lineRow.Controls.Add(btnAddLine)
        AddRow(top, "Add product", lineRow)

        Dim summary As New TableLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .ColumnCount = 4, .RowCount = 2, .Padding = New Padding(16, 8, 16, 8)}
        summary.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        summary.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        summary.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        summary.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        chkVat.Text = $"Add VAT ({ConfiguredVatRate:0.##}%)"
        AddPair(summary, 0, 0, "Discount %", numDiscountPct)
        AddPair(summary, 1, 0, "VAT", chkVat)
        AddPair(summary, 0, 2, "Subtotal", lblSubtotal)
        AddPair(summary, 1, 2, "Discount", lblDiscount)
        AddPair(summary, 2, 0, "VAT amount", lblVat)
        AddPair(summary, 3, 0, "TOTAL", lblTotal)

        Dim gridHost As New Panel() With {.Dock = DockStyle.Fill}
        gridHost.Controls.Add(gridLines)
        Dim gridBar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 4, 16, 4)}
        gridBar.Controls.Add(btnRemoveLine)
        gridBar.Controls.Add(btnEditPrice)
        gridHost.Controls.Add(gridBar)

        UiHelpers.AddOkCancelRow(Me, "Save quotation", AddressOf btnSave_Click)
        Controls.Add(gridHost)
        Controls.Add(summary)
        Controls.Add(top)
        summary.BringToFront()
        gridHost.BringToFront()
        AddHandler gridLines.DataBindingComplete, Sub(s, e)
                                                      If gridLines.Columns.Contains("ProductID") Then gridLines.Columns("ProductID").Visible = False
                                                  End Sub
    End Sub

    Private Shared Sub AddRow(t As TableLayoutPanel, label As String, ctl As Control)
        Dim row = t.RowCount
        t.RowCount += 1
        t.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        t.Controls.Add(New Label() With {.Text = label & ":", .AutoSize = True, .Margin = New Padding(0, 8, 8, 6)}, 0, row)
        t.Controls.Add(ctl, 1, row)
    End Sub

    Private Shared Sub AddPair(t As TableLayoutPanel, row As Integer, col As Integer, label As String, ctl As Control)
        t.Controls.Add(New Label() With {.Text = label & ":", .AutoSize = True, .Margin = New Padding(If(col = 0, 0, 24), 8, 8, 4)}, col, row)
        ctl.Margin = New Padding(0, 4, 0, 4)
        t.Controls.Add(ctl, col + 1, row)
    End Sub

    Private Sub btnNewCustomer_Click(sender As Object, e As EventArgs)
        Using f As New frmAddCustomer()
            If f.ShowDialog() = DialogResult.OK Then
                Dim newId = DataAccess.ExecuteScalarInsert(
                    "INSERT INTO Customers (Name, CustomerType, ContactName, Phone, Email, [Address], Location, TaxID, RebateRatePct, CreditLimit, Balance) " &
                    "VALUES (@n, @t, @c, @p, @e, @a, @l, @x, @rb, @cl, 0)", f.Params())
                ReloadCustomers()
                cboCustomer.SelectedValue = newId
            End If
        End Using
    End Sub

    Private Function QtyAlreadyOnQuotation(productId As Integer) As Integer
        Dim rows = lineItems.Select("ProductID = " & productId)
        Return If(rows.Length = 0, 0, CInt(rows(0)("Qty")))
    End Function

    Private Sub btnAddLine_Click(sender As Object, e As EventArgs)
        Dim row = TryCast(cboProduct.SelectedItem, DataRowView)
        If row Is Nothing Then Return
        Dim productId = CInt(row("ProductID"))
        Dim price = TierPrice(productId)
        Dim qty = CInt(numQty.Value)

        Dim existing = lineItems.Select("ProductID = " & productId)
        If existing.Length > 0 Then
            existing(0)("Qty") = CInt(existing(0)("Qty")) + qty
            existing(0)("LineTotal") = CInt(existing(0)("Qty")) * CDec(existing(0)("UnitPrice"))
        Else
            lineItems.Rows.Add(productId, Convert.ToString(row("Name")), qty, price, qty * price)
        End If
        RecalculateTotals()
    End Sub

    ''' Overrides the selected line's price — never touches stock, and is
    ''' logged (see Quotations.SaveOnce) once the quotation itself is saved.
    Private Sub btnEditPrice_Click(sender As Object, e As EventArgs)
        If gridLines.CurrentRow Is Nothing OrElse gridLines.CurrentRow.Index >= lineItems.Rows.Count Then Return
        Dim row = lineItems.Rows(gridLines.CurrentRow.Index)
        Dim productId = CInt(row("ProductID"))
        Dim standard = TierPrice(productId)
        Using f As New frmEditPrice(Convert.ToString(row("Product")), standard, CDec(row("UnitPrice")))
            If f.ShowDialog(Me) = DialogResult.OK Then
                row("UnitPrice") = f.NewPrice
                row("LineTotal") = f.NewPrice * CInt(row("Qty"))
                RecalculateTotals()
            End If
        End Using
    End Sub

    Private _total As Decimal

    Private Sub RecalculateTotals()
        Dim subtotal = lineItems.AsEnumerable().Sum(Function(r) CDec(r("LineTotal")))
        Dim discountAmt = subtotal * (numDiscountPct.Value / 100D)
        Dim vatable = subtotal - discountAmt
        Dim vat = vatable * (VatRate / 100D)
        _total = vatable + vat
        lblSubtotal.Text = AppInfo.Money(subtotal)
        lblDiscount.Text = AppInfo.Money(discountAmt)
        lblVat.Text = If(chkVat.Checked, AppInfo.Money(vat), "not charged")
        lblTotal.Text = AppInfo.Money(_total)
    End Sub

    Private Sub btnSave_Click(sender As Object, e As EventArgs)
        Dim customerRow = TryCast(cboCustomer.SelectedItem, DataRowView)
        If customerRow Is Nothing OrElse lineItems.Rows.Count = 0 Then
            AppUI.Info(Me, "Pick a customer and add at least one product line.")
            Return
        End If

        Dim req As New Quotations.QuoteRequest() With {
            .CustomerID = CInt(customerRow("CustomerID")),
            .QuotationDate = Date.Today,
            .PriceTier = cboTier.Text,
            .DiscountPct = numDiscountPct.Value,
            .VatRate = VatRate}

        For Each r As DataRow In lineItems.Rows
            req.Lines.Add(New Quotations.QuoteLine() With {
                .ProductID = CInt(r("ProductID")),
                .ProductName = Convert.ToString(r("Product")),
                .Quantity = CInt(r("Qty")),
                .UnitPrice = CDec(r("UnitPrice"))})
        Next

        Try
            Dim saved = Quotations.Save(req, currentUserId)
            _SavedQuotationId = saved.QuotationID
            AppUI.Toast($"Quotation {saved.QuotationNumber} saved — {AppInfo.Money(saved.Total)}.", AppUI.ToastKind.Success)
            Me.DialogResult = DialogResult.OK
            Me.Close()
        Catch ex As Exception
            AppUI.Toast("Could not save quotation: " & ex.Message, AppUI.ToastKind.Error)
        End Try
    End Sub
End Class
