Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data

''' New sale. Pick the customer (their ranking sets the default price tier —
''' Distributor / Wholesaler / Retailer / Walk-in), pick the fulfilling warehouse
''' (Lawal / Shore), add product lines priced from the chosen tier, then record
''' how much was paid now and (if part-paid) the due date. On save it writes the
''' invoice + lines, deducts stock from the chosen warehouse, updates the
''' customer balance + ledger for any unpaid amount, and accrues the customer's
''' rebate (default 1% of net sales, walk-ins excluded).
Public Class frmNewInvoice
    Inherits Form

    Private ReadOnly currentUserId As Integer
    ' Sale date — set it back to the day the goods actually went out.
    Private dtpSaleDate As New DateTimePicker() With {.Format = DateTimePickerFormat.Custom, .CustomFormat = "dd MMM yyyy", .Width = 140}
    Private cboCustomer As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 240}
    Private btnNewCustomer As New Button() With {.Text = "+ New", .AutoSize = True}
    Private cboTier As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 140}
    Private cboWarehouse As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 160}
    Private cboProduct As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 260}
    Private numQty As New NumericUpDown() With {.Minimum = 1, .Maximum = 1000000, .Value = 1}
    Private lblUnit As New Label() With {.AutoSize = True, .Tag = "keepfont", .Margin = New Padding(8, 8, 0, 0)}
    Private btnAddLine As New Button() With {.Text = "Add line", .Tag = "primary", .AutoSize = True}
    ' A hand scanner is a keyboard: it types the code then presses Enter, so all
    ' this box has to do is act on Enter.
    Private txtScan As New TextBox() With {.Width = 260}
    ' What the algorithms noticed about the sale being keyed — upsell, or a
    ' warning that this leaves the shelf thin. Hidden until there is something.
    Private lblAdvice As New Label() With {.AutoSize = True, .Visible = False, .Tag = "keepfont", .Padding = New Padding(4, 2, 4, 6)}
    ' K-Means segment for the customer on this sale, from how they actually buy.
    Private lblSegment As New Label() With {.AutoSize = True, .Visible = False, .Tag = "keepfont", .Margin = New Padding(8, 8, 0, 0)}
    Private gridLines As DataGridView = UiHelpers.NewGrid()
    Private btnRemoveLine As New Button() With {.Text = "Remove line", .AutoSize = True, .Enabled = False}
    Private btnEditPrice As New Button() With {.Text = "Edit price", .AutoSize = True, .Enabled = False}
    Private cboPaymentMethod As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 160}
    Private numDiscountPct As New NumericUpDown() With {.Maximum = 100, .DecimalPlaces = 1}
    Private numPaidNow As New NumericUpDown() With {.Maximum = 1000000000, .DecimalPlaces = 2}
    Private dtpDue As New DateTimePicker() With {.Format = DateTimePickerFormat.Short, .Value = DateTime.Today.AddDays(30)}
    Private lblSubtotal As New Label() With {.AutoSize = True, .Tag = "keepfont"}
    Private lblDiscount As New Label() With {.AutoSize = True, .Tag = "keepfont"}
    Private lblVat As New Label() With {.AutoSize = True, .Tag = "keepfont"}
    ' VAT is only charged when the user ticks this — off by default.
    Private chkVat As New CheckBox() With {.AutoSize = True, .Checked = False}
    Private lblTotal As New Label() With {.AutoSize = True, .Tag = "keepfont", .Font = New Font("Segoe UI", 13, FontStyle.Bold)}
    Private lblPrevBalance As New Label() With {.AutoSize = True, .Tag = "keepfont"}
    Private lblBalance As New Label() With {.AutoSize = True, .Tag = "keepfont"}

    Private lineItems As New DataTable()
    Private prices As DataTable   ' ProductID, Name, CostPrice, PriceDistributor, PriceWholesaler, PriceRetail
    Private ReadOnly ConfiguredVatRate As Decimal = AppInfo.VatRate
    ''' What the selected customer already owed before this sale — fetched
    ''' whenever the customer changes, so it's never left out of what's shown
    ''' or what's suggested to collect. The save itself re-reads this fresh
    ''' (and locked) inside the transaction; this copy is only for the screen.
    Private _customerBalance As Decimal = 0D
    ''' What the seller chose, in frmPreviousDebt, to collect toward that old
    ''' balance alongside today's sale. 0 unless they explicitly said to.
    Private _debtToCollectNow As Decimal = 0D
    ''' The customer last prompted about — so switching tiers or re-triggering
    ''' a sync doesn't re-open the same prompt on top of itself.
    Private _lastPromptedCustomerId As Integer = -1
    ''' False until the constructor finishes — an indebted customer being the
    ''' default selection shouldn't pop a dialog before the form itself is even
    ''' visible; the prompt is for a customer the seller actually picked.
    Private _formReady As Boolean = False

    Private ReadOnly Property VatRate As Decimal
        Get
            Return If(chkVat.Checked, ConfiguredVatRate, 0D)
        End Get
    End Property

    Public ReadOnly Property SavedInvoiceId As Integer

    ''' `fromQuotationId` pre-fills the customer, price tier and lines from an
    ''' existing quotation being converted — still fully editable here (add,
    ''' remove, replace, reprice) before it becomes a real sale. The caller
    ''' (ucQuotations) marks that quotation Converted itself once this saves.
    Public Sub New(userId As Integer, Optional fromQuotationId As Integer? = Nothing)
        currentUserId = userId
        Text = "New sale"
        Width = 820
        Height = 720
        StartPosition = FormStartPosition.CenterParent

        ReloadCustomers()

        cboTier.Items.AddRange({"Distributor", "Wholesaler", "Retailer", "Walk-in"})
        cboTier.SelectedIndex = 2

        cboWarehouse.DataSource = DataAccess.GetTable("SELECT WarehouseID, Name FROM Warehouses ORDER BY Name")
        cboWarehouse.DisplayMember = "Name"
        cboWarehouse.ValueMember = "WarehouseID"

        prices = DataAccess.GetTable("SELECT ProductID, Name, CostPrice, PriceDistributor, PriceWholesaler, PriceRetail FROM Products WHERE IsActive = 1 ORDER BY Name")
        cboProduct.DataSource = prices
        cboProduct.DisplayMember = "Name"
        cboProduct.ValueMember = "ProductID"

        cboPaymentMethod.Items.AddRange({"Cash", "Bank Transfer", "Card", "Credit"})
        cboPaymentMethod.SelectedIndex = 0

        lineItems.Columns.Add("ProductID", GetType(Integer))
        lineItems.Columns.Add("Product", GetType(String))
        lineItems.Columns.Add("Qty", GetType(Integer))
        lineItems.Columns.Add("UnitPrice", GetType(Decimal))
        lineItems.Columns.Add("LineTotal", GetType(Decimal))
        gridLines.DataSource = lineItems

        BuildLayout()

        AddHandler cboCustomer.SelectedIndexChanged, Sub(s, e) SyncTierToCustomer()
        AddHandler cboTier.SelectedIndexChanged, Sub(s, e)
                                                     RepriceLines()
                                                     UpdateUnitHint()
                                                 End Sub
        AddHandler cboProduct.SelectedIndexChanged, Sub(s, e) UpdateUnitHint()
        AddHandler cboWarehouse.SelectedIndexChanged, Sub(s, e) UpdateUnitHint()
        AddHandler btnNewCustomer.Click, AddressOf btnNewCustomer_Click
        AddHandler btnAddLine.Click, AddressOf btnAddLine_Click
        AddHandler txtScan.KeyDown, AddressOf Scan_KeyDown
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
        AddHandler numPaidNow.ValueChanged, Sub(s, e) RecalculateTotals()
        AddHandler cboPaymentMethod.SelectedIndexChanged, Sub(s, e) RecalculateTotals()
        AddHandler chkVat.CheckedChanged, Sub(s, e) RecalculateTotals()

        If fromQuotationId.HasValue Then LoadFromQuotation(fromQuotationId.Value)

        SyncTierToCustomer()
        UpdateUnitHint()
        UiHelpers.FitToScreen(Me)
        Theme.Apply(Me)
        _formReady = True
    End Sub

    ''' Seeds the customer, tier and lines from a quotation. Lines are added
    ''' straight from the quotation's own saved prices (which may already
    ''' carry an override) rather than through btnAddLine_Click, so nothing
    ''' here silently reprices them back to the plain tier price.
    Private Sub LoadFromQuotation(quotationId As Integer)
        Dim header = DataAccess.GetTable(
            "SELECT CustomerID, PriceTier FROM Quotations WHERE QuotationID = @id",
            New Dictionary(Of String, Object) From {{"@id", quotationId}})
        If header.Rows.Count = 0 Then Return
        cboCustomer.SelectedValue = Convert.ToInt32(header.Rows(0)("CustomerID"))
        Dim tier = Convert.ToString(header.Rows(0)("PriceTier"))
        If cboTier.Items.Contains(tier) Then cboTier.SelectedItem = tier

        Dim items = DataAccess.GetTable(
            "SELECT qi.ProductID, p.Name AS Product, qi.Quantity, qi.UnitPrice " &
            "FROM QuotationItems qi JOIN Products p ON p.ProductID = qi.ProductID WHERE qi.QuotationID = @id",
            New Dictionary(Of String, Object) From {{"@id", quotationId}})
        For Each r As DataRow In items.Rows
            Dim qty = Convert.ToInt32(r("Quantity"))
            Dim price = Convert.ToDecimal(r("UnitPrice"))
            lineItems.Rows.Add(Convert.ToInt32(r("ProductID")), Convert.ToString(r("Product")), qty, price, qty * price)
        Next
        RecalculateTotals()
    End Sub

    ''' A customer who already owes is flagged right in the list — "special
    ''' preference" starts before they're even selected, not after.
    Private Sub ReloadCustomers()
        Dim t = DataAccess.GetTable("SELECT CustomerID, Name, CustomerType, Balance FROM Customers ORDER BY Name")
        t.Columns.Add("DisplayName", GetType(String))
        For Each r As DataRow In t.Rows
            Dim bal = Convert.ToDecimal(r("Balance"))
            r("DisplayName") = If(bal > 0, $"{r("Name")}  —  owes {AppInfo.Money(bal)}", Convert.ToString(r("Name")))
        Next
        cboCustomer.DataSource = t
        cboCustomer.DisplayMember = "DisplayName"
        cboCustomer.ValueMember = "CustomerID"
    End Sub

    Private Sub SyncTierToCustomer()
        Dim row = TryCast(cboCustomer.SelectedItem, DataRowView)
        If row Is Nothing Then
            _customerBalance = 0D
            _debtToCollectNow = 0D
            RecalculateTotals()
            Return
        End If
        Dim customerId = CInt(row("CustomerID"))
        ' Fetched before the tier is applied, so if setting the tier fires its
        ' own recalculation, it already sees this customer's balance and not
        ' whichever customer was picked before.
        _customerBalance = FetchCustomerBalance(customerId)
        Dim t = Convert.ToString(row("CustomerType"))
        If cboTier.Items.Contains(t) Then cboTier.SelectedItem = t
        ShowCustomerSegment(customerId)

        ' A customer who already owes is put in front of the seller right away —
        ' not left as a quiet figure they might not think to check.
        If _customerBalance <= 0 Then
            _debtToCollectNow = 0D
        ElseIf _formReady AndAlso customerId <> _lastPromptedCustomerId Then
            PromptForPreviousDebt(customerId, Convert.ToString(row("Name")))
        End If

        RecalculateTotals()
    End Sub

    ''' What this customer already owes, so a new sale never quietly leaves an
    ''' old debt out of what the seller is shown or asked to collect.
    Private Function FetchCustomerBalance(customerId As Integer) As Decimal
        Try
            Dim t = DataAccess.GetTable("SELECT Balance FROM Customers WHERE CustomerID = @id",
                New Dictionary(Of String, Object) From {{"@id", customerId}})
            Return If(t.Rows.Count = 0, 0D, Convert.ToDecimal(t.Rows(0)(0)))
        Catch
            Return 0D
        End Try
    End Function

    ''' How many of this customer's invoices are still unpaid, and the oldest of
    ''' them — the context that turns "they owe ₦40,000" into something the
    ''' seller can actually act on.
    Private Function FetchDebtContext(customerId As Integer) As (Count As Integer, Oldest As Date?)
        Try
            Dim t = DataAccess.GetTable(
                "SELECT COUNT(*) AS N, MIN(InvoiceDate) AS Oldest FROM Invoices WHERE CustomerID = @id AND [Status] <> 'Paid'",
                New Dictionary(Of String, Object) From {{"@id", customerId}})
            If t.Rows.Count = 0 OrElse t.Rows(0)("N") Is DBNull.Value Then Return (0, Nothing)
            Dim oldest As Date? = If(t.Rows(0)("Oldest") Is DBNull.Value, CType(Nothing, Date?), Convert.ToDateTime(t.Rows(0)("Oldest")))
            Return (Convert.ToInt32(t.Rows(0)("N")), oldest)
        Catch
            Return (0, Nothing)
        End Try
    End Function

    ''' Shows the previous-balance prompt for a customer just picked. Marks them
    ''' as prompted either way, so switching tiers or re-syncing doesn't nag
    ''' twice — and if the seller skips it, says precisely what that means for
    ''' this customer rather than leaving it to be inferred.
    Private Sub PromptForPreviousDebt(customerId As Integer, customerName As String)
        _lastPromptedCustomerId = customerId
        Dim ctx = FetchDebtContext(customerId)
        Using f As New frmPreviousDebt(customerName, _customerBalance, ctx.Count, ctx.Oldest)
            f.ShowDialog(Me)
            If f.WillCollect Then
                _debtToCollectNow = f.Amount
                AppUI.Toast($"{AppInfo.Money(f.Amount)} of {customerName}'s previous balance will be collected with this sale.", AppUI.ToastKind.Success)
            Else
                _debtToCollectNow = 0D
                Dim since = If(ctx.Oldest.HasValue, $" The oldest is from {ctx.Oldest.Value:dd MMM yyyy}.", "")
                AppUI.Info(Me,
                    $"{customerName} still owes {AppInfo.Money(_customerBalance)} from {ctx.Count} earlier unpaid invoice(s).{since}" &
                    vbCrLf & vbCrLf &
                    "This sale will go ahead without collecting it — the balance stays exactly as it is on their account.",
                    "Previous balance left as is")
            End If
        End Using
    End Sub

    ''' Which kind of customer this is, worked out from how they actually buy
    ''' (how often, how much a time, how much in total) rather than from the
    ''' tier someone typed on their record years ago.
    Private Sub ShowCustomerSegment(customerId As Integer)
        Try
            Dim segment = SaleAdvice.CustomerSegment(customerId)
            lblSegment.Text = If(segment Is Nothing, "", "This is " & segment)
            lblSegment.Visible = segment IsNot Nothing
        Catch
            lblSegment.Visible = False
        End Try
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

    ''' The warehouse the sale draws from first, or 0 before one is picked.
    Private ReadOnly Property SelectedWarehouseId As Integer
        Get
            Return If(cboWarehouse.SelectedValue Is Nothing, 0, CInt(cboWarehouse.SelectedValue))
        End Get
    End Property

    ''' Units already spoken for by lines on this invoice — so a second line for
    ''' the same product is measured against what the first one leaves behind.
    Private Function QtyAlreadyOnInvoice(productId As Integer) As Integer
        Dim rows = lineItems.Select("ProductID = " & productId)
        Return If(rows.Length = 0, 0, CInt(rows(0)("Qty")))
    End Function

    Private Sub UpdateUnitHint()
        Dim row = TryCast(cboProduct.SelectedItem, DataRowView)
        If row Is Nothing Then
            lblUnit.Text = ""
            Return
        End If

        Dim productId = CInt(row("ProductID"))
        Dim price = "@ " & AppInfo.Money(TierPrice(productId)) & "  (" & cboTier.Text & ")"
        If SelectedWarehouseId = 0 Then
            lblUnit.Text = price
            Return
        End If

        Try
            Dim have = Stock.AvailabilityFor(productId, SelectedWarehouseId)
            Dim free = have.Total - QtyAlreadyOnInvoice(productId)
            Dim stockText = $"   ·   {free} in stock"
            If have.Elsewhere > 0 AndAlso have.InWarehouse < free Then
                stockText &= $" ({have.InWarehouse} here, {have.Elsewhere} other warehouse)"
            End If
            lblUnit.Text = price & stockText
            lblUnit.ForeColor = If(free <= 0, Drawing.Color.Firebrick, Theme.Current.TextMuted)
        Catch
            lblUnit.Text = price   ' stock lookup is only a hint — never block the screen
        End Try
    End Sub

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
        custRow.Controls.Add(New Label() With {.Text = "  Sale date:", .AutoSize = True, .Margin = New Padding(12, 8, 4, 0)})
        custRow.Controls.Add(dtpSaleDate)
        lblSegment.ForeColor = Theme.Current.TextMuted
        custRow.Controls.Add(lblSegment)
        AddRow(top, "Customer", custRow)

        Dim tierRow As New FlowLayoutPanel() With {.AutoSize = True}
        tierRow.Controls.Add(cboTier)
        tierRow.Controls.Add(New Label() With {.Text = "  Warehouse:", .AutoSize = True, .Margin = New Padding(8, 8, 4, 0)})
        tierRow.Controls.Add(cboWarehouse)
        AddRow(top, "Price tier", tierRow)

        Dim lineRow As New FlowLayoutPanel() With {.AutoSize = True}
        lineRow.Controls.Add(cboProduct)
        lineRow.Controls.Add(New Label() With {.Text = "Qty:", .AutoSize = True, .Margin = New Padding(10, 8, 4, 0)})
        lineRow.Controls.Add(numQty)
        lineRow.Controls.Add(lblUnit)
        lineRow.Controls.Add(btnAddLine)
        AddRow(top, "Add product", lineRow)

        Dim scanRow As New FlowLayoutPanel() With {.AutoSize = True}
        scanRow.Controls.Add(txtScan)
        scanRow.Controls.Add(New Label() With {
            .Text = "Scan a product barcode — it's added straight to the sale.",
            .AutoSize = True, .Margin = New Padding(10, 8, 0, 0), .ForeColor = Theme.Current.TextMuted})
        AddRow(top, "Scan", scanRow)

        lblAdvice.MaximumSize = New Size(700, 0)
        lblAdvice.ForeColor = Theme.Current.Primary
        AddRow(top, "", lblAdvice)

        ' Two columns (inputs | totals) so the summary stays short on small screens.
        Dim summary As New TableLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .ColumnCount = 4, .RowCount = 6, .Padding = New Padding(16, 8, 16, 8)}
        summary.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        summary.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        summary.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        summary.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        For i = 1 To 6
            summary.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Next
        chkVat.Text = $"Add VAT ({ConfiguredVatRate:0.##}%)"
        AddPair(summary, 0, 0, "Payment method", cboPaymentMethod)
        AddPair(summary, 1, 0, "Discount %", numDiscountPct)
        AddPair(summary, 2, 0, "VAT", chkVat)
        AddPair(summary, 3, 0, "Amount paid now", numPaidNow)
        AddPair(summary, 4, 0, "Balance due date", dtpDue)
        AddPair(summary, 0, 2, "Subtotal", lblSubtotal)
        AddPair(summary, 1, 2, "Discount", lblDiscount)
        AddPair(summary, 2, 2, "VAT amount", lblVat)
        AddPair(summary, 3, 2, "TOTAL (this sale)", lblTotal)
        ' Previous balance is never left off the summary — this is exactly the
        ' figure a clerk needs to know they should also be collecting.
        AddPair(summary, 4, 2, "Previous balance owed", lblPrevBalance)
        AddPair(summary, 5, 2, "Balance after this sale", lblBalance)

        Dim gridHost As New Panel() With {.Dock = DockStyle.Fill}
        gridHost.Controls.Add(gridLines)
        Dim gridBar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 4, 16, 4)}
        gridBar.Controls.Add(btnRemoveLine)
        gridBar.Controls.Add(btnEditPrice)
        gridHost.Controls.Add(gridBar)

        UiHelpers.AddOkCancelRow(Me, "Save sale", AddressOf btnSave_Click)
        Controls.Add(gridHost)
        Controls.Add(summary)
        Controls.Add(top)
        ' Dock order: top strip, Save/Cancel at the very bottom, summary above it, grid fills the rest.
        summary.BringToFront()
        gridHost.BringToFront()
        AddHandler gridLines.DataBindingComplete, Sub(s, e)
                                                      If gridLines.Columns.Contains("ProductID") Then gridLines.Columns("ProductID").Visible = False
                                                  End Sub

        RecalculateTotals()
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

    ''' A scanner types the code and presses Enter. Look the code up, select that
    ''' product and add it — one scan is one unit, scan again for the next, which
    ''' is how a till is expected to behave.
    Private Sub Scan_KeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode <> Keys.Enter Then Return
        ' Stop the Enter reaching the form's default button and saving the sale.
        e.Handled = True
        e.SuppressKeyPress = True

        Dim scan = Barcodes.ReadScan(txtScan.Text)
        txtScan.Clear()
        If scan Is Nothing Then Return

        Dim match = DataAccess.GetTable(
            "SELECT TOP 1 ProductID, Name FROM Products WHERE Barcode = @code AND IsActive = 1",
            New Dictionary(Of String, Object) From {{"@code", scan.Code}})
        If match.Rows.Count = 0 Then
            AppUI.Toast($"No product carries the barcode {scan.Code}.", AppUI.ToastKind.Warning)
            Return
        End If

        cboProduct.SelectedValue = Convert.ToInt32(match.Rows(0)("ProductID"))
        numQty.Value = 1
        btnAddLine_Click(Nothing, EventArgs.Empty)
        txtScan.Focus()
    End Sub

    Private Sub btnAddLine_Click(sender As Object, e As EventArgs)
        Dim row = TryCast(cboProduct.SelectedItem, DataRowView)
        If row Is Nothing Then Return
        Dim productId = CInt(row("ProductID"))
        Dim price = TierPrice(productId)
        Dim qty = CInt(numQty.Value)

        ' Caught at entry rather than at save, so the seller fixes one line while
        ' they're still looking at it.
        If Not ConfirmLineIsInStock(productId, Convert.ToString(row("Name")), qty) Then Return

        ' A quantity far outside what normally goes out for this product is more
        ' often a keying slip than a big order, and after saving it has already
        ' come off the shelf.
        Dim odd = SaleAdvice.QuantityLooksUnusual(productId, qty)
        If odd IsNot Nothing AndAlso
           Not AppUI.Confirm(Me, odd, "Check the quantity", "Add it anyway") Then Return

        Dim existing = lineItems.Select("ProductID = " & productId)
        If existing.Length > 0 Then
            existing(0)("Qty") = CInt(existing(0)("Qty")) + qty
            existing(0)("LineTotal") = CInt(existing(0)("Qty")) * price
        Else
            lineItems.Rows.Add(productId, Convert.ToString(row("Name")), qty, price, qty * price)
        End If
        RecalculateTotals()
        UpdateUnitHint()
        ShowSaleAdvice(productId, qty)
    End Sub

    ''' Overrides the selected line's price. The tier price stays what it was
    ''' computed at — this only changes what's charged, and the override is
    ''' logged (see Sales.SaveOnce) once the sale itself is saved.
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

    ''' The advice line under the sale: what usually goes with what's just been
    ''' added, and whether this sale leaves the shelf too thin. One line at a
    ''' time — a panel of warnings at a till gets ignored wholesale.
    Private Sub ShowSaleAdvice(productId As Integer, qty As Integer)
        Try
            Dim onSale = lineItems.AsEnumerable().Select(Function(r) CInt(r("ProductID"))).ToList()
            Dim message = SaleAdvice.Upsell(onSale)
            If message Is Nothing Then message = SaleAdvice.StockoutRiskAfterSale(productId, qty)

            lblAdvice.Text = If(message, "")
            lblAdvice.Visible = message IsNot Nothing
        Catch
            ' Advice is a courtesy — never let it stop a sale being keyed.
            lblAdvice.Visible = False
        End Try
    End Sub

    ''' True when the line can be added. Stock trouble is reported here with the
    ''' restocking advice, so the seller knows what to order rather than just
    ''' that they can't sell it.
    Private Function ConfirmLineIsInStock(productId As Integer, productName As String, qty As Integer) As Boolean
        If SelectedWarehouseId = 0 Then Return True
        Try
            Dim have = Stock.AvailabilityFor(productId, SelectedWarehouseId)
            Dim free = have.Total - QtyAlreadyOnInvoice(productId)
            If qty <= free Then Return True

            Dim advice = Stock.AdviseRestock(productId)
            Dim msg = $"Only {Math.Max(0, free)} of {productName} left in stock, but this line asks for {qty}."
            If advice IsNot Nothing Then msg &= vbCrLf & vbCrLf & "Restock: " & advice.Summary
            AppUI.Info(Me, msg)
            Return False
        Catch
            Return True   ' never block a sale because the stock lookup failed
        End Try
    End Function

    Private _total As Decimal

    Private Sub RecalculateTotals()
        Dim subtotal = lineItems.AsEnumerable().Sum(Function(r) CDec(r("LineTotal")))
        Dim discountAmt = subtotal * (numDiscountPct.Value / 100D)
        Dim vatable = subtotal - discountAmt
        Dim vat = vatable * (VatRate / 100D)
        _total = vatable + vat
        Dim combinedDue = _customerBalance + _total
        ' Suggested payment is today's sale plus whatever the seller explicitly
        ' chose, in the previous-balance prompt, to collect toward the old debt —
        ' not the whole old balance by default. The prompt is what decides that;
        ' this only carries the decision through as lines are added.
        Dim suggestedPaid = _total + _debtToCollectNow

        If cboPaymentMethod.Text = "Credit" Then
            ' leave numPaidNow as the user set it (0 by default)
        ElseIf numPaidNow.Value = 0D OrElse numPaidNow.Value = _lastAutoFilled Then
            numPaidNow.Value = Math.Min(suggestedPaid, numPaidNow.Maximum)
        End If
        _lastAutoFilled = numPaidNow.Value

        ' Today's sale is settled first out of whatever's paid; anything left
        ' over after that is what still remains, old debt included.
        Dim balanceAfter = Math.Max(0D, combinedDue - numPaidNow.Value)
        lblSubtotal.Text = AppInfo.Money(subtotal)
        lblDiscount.Text = AppInfo.Money(discountAmt)
        lblVat.Text = If(chkVat.Checked, AppInfo.Money(vat), "not charged")
        lblTotal.Text = AppInfo.Money(_total)
        lblPrevBalance.Text = AppInfo.Money(_customerBalance)
        lblPrevBalance.ForeColor = If(_customerBalance > 0, Theme.Current.Danger, Theme.Current.TextMuted)
        lblBalance.Text = AppInfo.Money(balanceAfter)
        lblBalance.ForeColor = If(balanceAfter > 0, Theme.Current.Danger, Theme.Current.TextPrimary)
        dtpDue.Enabled = balanceAfter > 0
    End Sub
    Private _lastAutoFilled As Decimal = -1

    Private Sub btnSave_Click(sender As Object, e As EventArgs)
        Dim customerRow = TryCast(cboCustomer.SelectedItem, DataRowView)
        If customerRow Is Nothing OrElse lineItems.Rows.Count = 0 Then
            AppUI.Info(Me, "Pick a customer and add at least one product line.")
            Return
        End If

        Dim req As New Sales.SaleRequest() With {
            .CustomerID = CInt(customerRow("CustomerID")),
            .CustomerName = Convert.ToString(customerRow("Name")),
            .CustomerType = Convert.ToString(customerRow("CustomerType")),
            .SaleDate = dtpSaleDate.Value.Date,
            .PriceTier = cboTier.Text,
            .WarehouseID = CInt(cboWarehouse.SelectedValue),
            .PaymentMethod = cboPaymentMethod.Text,
            .DiscountPct = numDiscountPct.Value,
            .VatRate = VatRate,
            .PaidNow = numPaidNow.Value,
            .DueDate = dtpDue.Value.Date}

        For Each r As DataRow In lineItems.Rows
            Dim pid = CInt(r("ProductID"))
            req.Lines.Add(New Sales.SaleLine() With {
                .ProductID = pid,
                .ProductName = Convert.ToString(r("Product")),
                .Quantity = CInt(r("Qty")),
                .UnitPrice = CDec(r("UnitPrice")),
                .UnitCost = Convert.ToDecimal(prices.Select("ProductID = " & pid)(0)("CostPrice"))})
        Next

        Try
            ' Checked before saving so the seller gets the shortfall and the
            ' restocking advice, not a bare database error.
            Dim shortfalls = Sales.FindShortfalls(req)
            If shortfalls.Count > 0 Then
                AppUI.Info(Me, New Sales.InsufficientStockException(shortfalls).Message)
                Return
            End If

            ' One transaction: invoice, lines, stock, ledger, payment and rebate
            ' all save together or not at all.
            Dim saved = Sales.Save(req, currentUserId)
            _SavedInvoiceId = saved.InvoiceID
            AppUI.Toast(SaleSummary(saved), AppUI.ToastKind.Success)
            Anim.SuccessTick(If(Owner, Me), "Sale saved")
            Me.DialogResult = DialogResult.OK
            Me.Close()
        Catch ex As Sales.InsufficientStockException
            ' Another till got there first between the check and the save.
            AppUI.Info(Me, ex.Message)
        Catch ex As Exception
            AppUI.Toast("Could not save sale: " & ex.Message, AppUI.ToastKind.Error)
        End Try
    End Sub

    ''' A one-line, but complete, account of what just happened — so a customer
    ''' with old debt is never quietly waved through as "Paid" while the debt
    ''' sits untouched and unmentioned.
    Private Shared Function SaleSummary(saved As Sales.SaleResult) As String
        Dim msg = $"Sale {saved.InvoiceNumber} saved — {AppInfo.Money(saved.Total)}."
        If saved.PreviousBalance > 0 Then
            If saved.AppliedToPreviousBalance > 0 Then
                msg &= $" {AppInfo.Money(saved.AppliedToPreviousBalance)} of the payment cleared part of their previous balance."
            End If
            msg &= If(saved.RemainingBalance > 0,
                $" They still owe {AppInfo.Money(saved.RemainingBalance)} in total (including earlier purchases).",
                " Their account is now fully settled.")
        ElseIf saved.Outstanding > 0 Then
            msg &= $" {AppInfo.Money(saved.Outstanding)} still owed on this sale."
        End If
        Return msg
    End Function
End Class
