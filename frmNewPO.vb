Imports System.Windows.Forms
Imports System.Data

''' New purchase order: pick supplier, add product/qty/cost lines, optionally add
''' VAT (off by default), say how much is being paid now, save header + lines +
''' Ledger entries for whatever we now owe the supplier and/or just paid off.
'''
''' A supplier we already owe from an earlier order is never quietly left out:
''' picking them prompts for whether to settle some of that debt alongside this
''' order (frmPreviousSupplierDebt), mirroring the New Sale screen's handling
''' of a customer who owes us.
Public Class frmNewPO
    Inherits Form

    Private ReadOnly currentUserId As Integer
    Private cboSupplier As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    ' Purchase date — set it back to the day the goods were actually bought.
    Private dtpOrderDate As New DateTimePicker() With {.Format = DateTimePickerFormat.Custom, .CustomFormat = "dd MMM yyyy", .Width = 140}
    Private cboProduct As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 260}
    Private numQty As New NumericUpDown() With {.Minimum = 1, .Maximum = 100000, .Value = 1}
    Private numUnitCost As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private btnAddLine As New Button() With {.Text = "Add line", .Tag = "primary", .AutoSize = True}
    Private btnSuggest As New Button() With {.Text = "Suggest order", .AutoSize = True}
    ''' What the forecast and the cost history have to say about this order.
    Private lblAdvice As New Label() With {.AutoSize = True, .Visible = False, .Tag = "keepfont"}
    Private btnNewSupplier As New Button() With {.Text = "+ New", .AutoSize = True}
    Private gridLines As DataGridView = UiHelpers.NewGrid()
    Private chkVat As New CheckBox() With {.AutoSize = True, .Checked = False, .Margin = New Padding(0, 4, 24, 0)}
    Private lblVat As New Label() With {.AutoSize = True, .Tag = "keepfont", .Margin = New Padding(0, 6, 24, 0)}
    Private lblTotal As New Label() With {.AutoSize = True, .Font = New Drawing.Font("Segoe UI", 12, Drawing.FontStyle.Bold)}
    Private cboPaymentMethod As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 140}
    Private numPaidNow As New NumericUpDown() With {.Maximum = 1000000000, .DecimalPlaces = 2}
    Private lblPrevBalance As New Label() With {.AutoSize = True, .Tag = "keepfont"}
    Private lblBalance As New Label() With {.AutoSize = True, .Tag = "keepfont"}

    Private lineItems As New DataTable()
    Private ReadOnly ConfiguredVatRate As Decimal = AppInfo.VatRate
    ''' Candid Purrfect buys what it sells: saving a purchase puts the goods in
    ''' stock straight away. ChewyPets orders now and receives later.
    Private ReadOnly ReceivesOnSave As Boolean = Not Company.Current.IsHome
    Private ReadOnly cboWarehouse As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 170}

    ''' What we already owed this supplier before this order — fetched whenever
    ''' the supplier changes. The save itself re-reads this fresh (and locked)
    ''' inside the transaction; this copy is only for the screen.
    Private _supplierBalance As Decimal = 0D
    ''' What was chosen, in frmPreviousSupplierDebt, to pay toward that old
    ''' balance alongside this order. 0 unless explicitly chosen.
    Private _debtToPayNow As Decimal = 0D
    Private _lastPromptedSupplierId As Integer = -1
    Private _lastAutoFilled As Decimal = -1
    ''' False until the constructor finishes — a supplier we owe being the
    ''' default selection shouldn't pop a dialog before the form is even visible.
    Private _formReady As Boolean = False

    Public Sub New(userId As Integer)
        currentUserId = userId
        Text = If(ReceivesOnSave, "Record purchase", "New purchase order")
        Width = 760
        Height = 660
        StartPosition = FormStartPosition.CenterParent

        ReloadSuppliers()
        cboPaymentMethod.Items.AddRange({"Cash", "Bank Transfer", "Card", "Credit"})
        cboPaymentMethod.SelectedIndex = 0

        cboProduct.DataSource = DataAccess.GetTable("SELECT ProductID, Name, CostPrice FROM Products WHERE IsActive = 1 ORDER BY Name")
        cboProduct.DisplayMember = "Name"
        cboProduct.ValueMember = "ProductID"
        AddHandler cboProduct.SelectedIndexChanged, Sub(s, e)
                                                         Dim row = CType(cboProduct.SelectedItem, DataRowView)
                                                         If row IsNot Nothing Then numUnitCost.Value = CDec(row("CostPrice"))
                                                     End Sub

        lineItems.Columns.Add("ProductID", GetType(Integer))
        lineItems.Columns.Add("ProductName", GetType(String))
        lineItems.Columns.Add("Qty", GetType(Integer))
        lineItems.Columns.Add("UnitCost", GetType(Decimal))
        gridLines.DataSource = lineItems

        Dim supplierRow As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 16, 16, 4)}
        supplierRow.Controls.Add(New Label() With {.Text = "Supplier:", .AutoSize = True, .Margin = New Padding(0, 8, 8, 0)})
        supplierRow.Controls.Add(cboSupplier)
        supplierRow.Controls.Add(btnNewSupplier)
        supplierRow.Controls.Add(New Label() With {.Text = "  Purchase date:", .AutoSize = True, .Margin = New Padding(12, 8, 4, 0)})
        supplierRow.Controls.Add(dtpOrderDate)
        If ReceivesOnSave Then
            Dim warehouses = DataAccess.GetTable("SELECT WarehouseID, Name FROM Warehouses ORDER BY WarehouseID")
            cboWarehouse.DataSource = warehouses
            cboWarehouse.DisplayMember = "Name"
            cboWarehouse.ValueMember = "WarehouseID"
            ' Only worth asking when there's a choice to make.
            If warehouses.Rows.Count > 1 Then
                supplierRow.Controls.Add(New Label() With {.Text = "  Into:", .AutoSize = True, .Margin = New Padding(12, 8, 4, 0)})
                supplierRow.Controls.Add(cboWarehouse)
            End If
        End If
        AddHandler btnNewSupplier.Click, Sub(s, e)
                                             Using f As New frmAddSupplier()
                                                 If f.ShowDialog() = DialogResult.OK Then
                                                     ReloadSuppliers()
                                                     cboSupplier.SelectedIndex = cboSupplier.FindStringExact(f.SupplierName)
                                                 End If
                                             End Using
                                         End Sub
        AddHandler cboSupplier.SelectedIndexChanged, Sub(s, e) SyncSupplierBalance()

        Dim lineRow As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 4, 16, 4)}
        lineRow.Controls.Add(New Label() With {.Text = "Product:", .AutoSize = True, .Margin = New Padding(0, 8, 8, 0)})
        lineRow.Controls.Add(cboProduct)
        lineRow.Controls.Add(New Label() With {.Text = "Qty:", .AutoSize = True, .Margin = New Padding(12, 8, 8, 0)})
        lineRow.Controls.Add(numQty)
        lineRow.Controls.Add(New Label() With {.Text = "Unit cost:", .AutoSize = True, .Margin = New Padding(12, 8, 8, 0)})
        lineRow.Controls.Add(numUnitCost)
        lineRow.Controls.Add(btnAddLine)
        lineRow.Controls.Add(btnSuggest)

        lblAdvice.MaximumSize = New Drawing.Size(720, 0)
        Dim adviceRow As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 0, 16, 4)}
        adviceRow.Controls.Add(lblAdvice)

        chkVat.Text = $"Add VAT ({ConfiguredVatRate:0.##}%)"
        Dim payRow As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .Padding = New Padding(16, 4, 16, 0)}
        payRow.Controls.Add(New Label() With {.Text = "Payment method:", .AutoSize = True, .Margin = New Padding(0, 8, 6, 0)})
        payRow.Controls.Add(cboPaymentMethod)
        payRow.Controls.Add(New Label() With {.Text = "  Amount to pay now:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
        payRow.Controls.Add(numPaidNow)
        payRow.Controls.Add(New Label() With {.Text = "  Previous balance owed:", .AutoSize = True, .Margin = New Padding(16, 8, 6, 0)})
        payRow.Controls.Add(lblPrevBalance)
        payRow.Controls.Add(New Label() With {.Text = "  Balance after this purchase:", .AutoSize = True, .Margin = New Padding(16, 8, 6, 0)})
        payRow.Controls.Add(lblBalance)

        Dim totalRow As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .Padding = New Padding(16)}
        totalRow.Controls.Add(chkVat)
        totalRow.Controls.Add(lblVat)
        totalRow.Controls.Add(New Label() With {.Text = "Total:", .AutoSize = True, .Margin = New Padding(0, 6, 4, 0)})
        totalRow.Controls.Add(lblTotal)

        UiHelpers.AddOkCancelRow(Me, If(ReceivesOnSave, "Save purchase & add to stock", "Save purchase order"), AddressOf btnSave_Click)
        Controls.Add(payRow)
        Controls.Add(totalRow)
        Controls.Add(gridLines)
        Controls.Add(adviceRow)
        Controls.Add(lineRow)
        Controls.Add(supplierRow)
        ' Dock order: Save/Cancel at the very bottom, total row above it, pay row
        ' above that, grid fills the rest.
        totalRow.BringToFront()
        payRow.BringToFront()
        gridLines.BringToFront()
        AddHandler gridLines.DataBindingComplete, Sub(s, e)
                                                      If gridLines.Columns.Contains("ProductID") Then gridLines.Columns("ProductID").Visible = False
                                                  End Sub

        AddHandler btnAddLine.Click, AddressOf btnAddLine_Click
        AddHandler btnSuggest.Click, AddressOf Suggest_Click
        AddHandler chkVat.CheckedChanged, Sub(s, e) RecalcTotal()
        AddHandler numPaidNow.ValueChanged, Sub(s, e) RecalcTotal()
        AddHandler cboPaymentMethod.SelectedIndexChanged, Sub(s, e) RecalcTotal()
        SyncSupplierBalance()
        RecalcTotal()
        UiHelpers.FitToScreen(Me)
        Theme.Apply(Me)
        _formReady = True
    End Sub

    ''' A supplier we already owe is flagged right in the list — visible before
    ''' they're even selected, the same way an indebted customer is on New Sale.
    Private Sub ReloadSuppliers()
        Dim t = DataAccess.GetTable("SELECT SupplierID, Name, Balance FROM Suppliers ORDER BY Name")
        t.Columns.Add("DisplayName", GetType(String))
        For Each r As DataRow In t.Rows
            Dim bal = Convert.ToDecimal(r("Balance"))
            r("DisplayName") = If(bal > 0, $"{r("Name")}  —  we owe {AppInfo.Money(bal)}", Convert.ToString(r("Name")))
        Next
        cboSupplier.DataSource = t
        cboSupplier.DisplayMember = "DisplayName"
        cboSupplier.ValueMember = "SupplierID"
    End Sub

    Private Sub SyncSupplierBalance()
        Dim row = TryCast(cboSupplier.SelectedItem, DataRowView)
        If row Is Nothing Then
            _supplierBalance = 0D
            _debtToPayNow = 0D
            RecalcTotal()
            Return
        End If
        Dim supplierId = CInt(row("SupplierID"))
        _supplierBalance = FetchSupplierBalance(supplierId)

        If _supplierBalance <= 0 Then
            _debtToPayNow = 0D
        ElseIf _formReady AndAlso supplierId <> _lastPromptedSupplierId Then
            PromptForPreviousSupplierDebt(supplierId, Convert.ToString(row("Name")))
        End If
        RecalcTotal()
    End Sub

    ''' What we already owe this supplier, fetched fresh so a new order never
    ''' quietly leaves an old debt out of what the seller is shown or asked to pay.
    Private Function FetchSupplierBalance(supplierId As Integer) As Decimal
        Try
            Dim t = DataAccess.GetTable("SELECT Balance FROM Suppliers WHERE SupplierID = @id",
                New Dictionary(Of String, Object) From {{"@id", supplierId}})
            Return If(t.Rows.Count = 0, 0D, Convert.ToDecimal(t.Rows(0)(0)))
        Catch
            Return 0D
        End Try
    End Function

    ''' How many of this supplier's orders are still unpaid, and the oldest of
    ''' them — turns "we owe ₦40,000" into something actionable.
    Private Function FetchSupplierDebtContext(supplierId As Integer) As (Count As Integer, Oldest As Date?)
        Try
            Dim t = DataAccess.GetTable(
                "SELECT COUNT(*) AS N, MIN(OrderDate) AS Oldest FROM PurchaseOrders WHERE SupplierID = @id AND PaymentStatus <> 'Paid'",
                New Dictionary(Of String, Object) From {{"@id", supplierId}})
            If t.Rows.Count = 0 OrElse t.Rows(0)("N") Is DBNull.Value Then Return (0, Nothing)
            Dim oldest As Date? = If(t.Rows(0)("Oldest") Is DBNull.Value, CType(Nothing, Date?), Convert.ToDateTime(t.Rows(0)("Oldest")))
            Return (Convert.ToInt32(t.Rows(0)("N")), oldest)
        Catch
            Return (0, Nothing)
        End Try
    End Function

    ''' Shows the previous-balance prompt for a supplier just picked. Marks them
    ''' as prompted either way, and if skipped, says precisely what that means.
    Private Sub PromptForPreviousSupplierDebt(supplierId As Integer, supplierName As String)
        _lastPromptedSupplierId = supplierId
        Dim ctx = FetchSupplierDebtContext(supplierId)
        Using f As New frmPreviousSupplierDebt(supplierName, _supplierBalance, ctx.Count, ctx.Oldest)
            f.ShowDialog(Me)
            If f.WillPay Then
                _debtToPayNow = f.Amount
                AppUI.Toast($"{AppInfo.Money(f.Amount)} toward {supplierName}'s previous balance will be paid with this purchase.", AppUI.ToastKind.Success)
            Else
                _debtToPayNow = 0D
                Dim since = If(ctx.Oldest.HasValue, $" The oldest is from {ctx.Oldest.Value:dd MMM yyyy}.", "")
                AppUI.Info(Me,
                    $"We still owe {supplierName} {AppInfo.Money(_supplierBalance)} from {ctx.Count} earlier unpaid order(s).{since}" &
                    vbCrLf & vbCrLf &
                    "This purchase will go ahead without paying it — the balance stays exactly as it is.",
                    "Previous balance left as is")
            End If
        End Using
    End Sub

    ''' Fills the order from the demand forecast: everything this supplier
    ''' carries that won't last the lead time, most urgent first. The buyer
    ''' reviews and edits — the screen's job is to stop them starting from a
    ''' blank sheet every month.
    Private Sub Suggest_Click(sender As Object, e As EventArgs)
        Dim supplier = TryCast(cboSupplier.SelectedItem, DataRowView)
        If supplier Is Nothing Then Return
        Dim supplierId = CInt(supplier("SupplierID"))

        Dim suggestions As List(Of PurchaseAdvice.SuggestedLine)
        Try
            suggestions = PurchaseAdvice.SuggestOrder(supplierId)
        Catch ex As Exception
            AppUI.Toast("Could not work out a suggestion: " & ex.Message, AppUI.ToastKind.Error)
            Return
        End Try

        If suggestions.Count = 0 Then
            AppUI.Info(Me, "Nothing from this supplier is forecast to run short before a delivery could arrive.")
            Return
        End If

        For Each s In suggestions
            If lineItems.Select("ProductID = " & s.ProductID).Length > 0 Then Continue For
            lineItems.Rows.Add(s.ProductID, s.ProductName, s.Quantity, s.UnitCost)
        Next
        RecalcTotal()

        Dim first = suggestions.First()
        Dim due = If(first.RunsOutOn.HasValue, $" {first.ProductName} runs out around {first.RunsOutOn.Value:dd MMM}.", "")
        ShowAdvice($"Suggested {suggestions.Count} line(s) from the demand forecast.{due} Edit anything that doesn't look right.")
    End Sub

    ''' What the algorithms noticed about the order as it stands.
    Private Sub ShowAdvice(message As String)
        lblAdvice.Text = If(message, "")
        lblAdvice.Visible = Not String.IsNullOrEmpty(message)
    End Sub

    Private Sub btnAddLine_Click(sender As Object, e As EventArgs)
        Dim row = CType(cboProduct.SelectedItem, DataRowView)
        If row Is Nothing Then Return
        Dim productId = CInt(row("ProductID"))
        Dim supplier = TryCast(cboSupplier.SelectedItem, DataRowView)

        ' A unit cost that doesn't belong with this supplier's others is either a
        ' typo or a price rise, and both are worth a second look before ordering.
        If supplier IsNot Nothing Then
            Dim odd = PurchaseAdvice.CostLooksUnusual(CInt(supplier("SupplierID")), productId, numUnitCost.Value)
            If odd IsNot Nothing AndAlso
               Not AppUI.Confirm(Me, odd, "Check the unit cost", "Order at this price") Then Return
        End If

        lineItems.Rows.Add(productId, row("Name").ToString(), CInt(numQty.Value), numUnitCost.Value)
        RecalcTotal()

        ' Half of a pair that sells together is stock that can't be sold as one.
        Try
            ShowAdvice(PurchaseAdvice.MissingPartner(
                lineItems.AsEnumerable().Select(Function(r) CInt(r("ProductID"))).ToList()))
        Catch
            ShowAdvice(Nothing)
        End Try
    End Sub

    Private Function Subtotal() As Decimal
        Return lineItems.AsEnumerable().Sum(Function(r) CInt(r("Qty")) * CDec(r("UnitCost")))
    End Function

    ''' VAT only when ticked.
    Private Function VatAmount() As Decimal
        Return If(chkVat.Checked, Math.Round(Subtotal() * ConfiguredVatRate / 100D, 2), 0D)
    End Function

    Private Sub RecalcTotal()
        lblVat.Text = If(chkVat.Checked, "VAT: " & AppInfo.Money(VatAmount()), "")
        Dim total = Subtotal() + VatAmount()
        lblTotal.Text = AppInfo.Money(total)

        Dim combinedDue = _supplierBalance + total
        ' Suggested payment is this order plus whatever was explicitly chosen,
        ' in the previous-balance prompt, to pay toward the old debt — not the
        ' whole old balance by default.
        Dim suggestedPaid = total + _debtToPayNow
        If cboPaymentMethod.Text = "Credit" Then
            ' leave numPaidNow as the user set it (0 by default)
        ElseIf numPaidNow.Value = 0D OrElse numPaidNow.Value = _lastAutoFilled Then
            numPaidNow.Value = Math.Min(suggestedPaid, numPaidNow.Maximum)
        End If
        _lastAutoFilled = numPaidNow.Value

        Dim balanceAfter = Math.Max(0D, combinedDue - numPaidNow.Value)
        lblPrevBalance.Text = AppInfo.Money(_supplierBalance)
        lblPrevBalance.ForeColor = If(_supplierBalance > 0, Theme.Current.Danger, Theme.Current.TextMuted)
        lblBalance.Text = AppInfo.Money(balanceAfter)
        lblBalance.ForeColor = If(balanceAfter > 0, Theme.Current.Danger, Theme.Current.TextPrimary)
    End Sub

    Private Sub btnSave_Click(sender As Object, e As EventArgs)
        Dim supplierRow = CType(cboSupplier.SelectedItem, DataRowView)
        If supplierRow Is Nothing OrElse lineItems.Rows.Count = 0 Then
            MessageBox.Show("Select a supplier and add at least one line item.", "Cannot save", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        Dim req As New Purchasing.PurchaseRequest() With {
            .SupplierID = CInt(supplierRow("SupplierID")),
            .SupplierName = supplierRow("Name").ToString(),
            .OrderDate = dtpOrderDate.Value.Date,
            .VatRate = If(chkVat.Checked, ConfiguredVatRate, 0D),
            .ReceiveNow = ReceivesOnSave,
            .WarehouseID = If(ReceivesOnSave AndAlso cboWarehouse.SelectedValue IsNot Nothing, Convert.ToInt32(cboWarehouse.SelectedValue), 0),
            .PaidNow = numPaidNow.Value,
            .PaymentMethod = cboPaymentMethod.Text}
        For Each r As DataRow In lineItems.Rows
            req.Lines.Add(New Purchasing.PurchaseLine() With {
                .ProductID = CInt(r("ProductID")),
                .ProductName = Convert.ToString(r("ProductName")),
                .Quantity = CInt(r("Qty")),
                .UnitCost = CDec(r("UnitCost"))})
        Next

        Try
            ' One transaction: header, lines and the ledger entries save together.
            Dim saved = Purchasing.Save(req, currentUserId)
            Dim msg As New Text.StringBuilder()
            If ReceivesOnSave Then
                msg.Append($"Purchase {saved.PONumber} saved — {req.Lines.Sum(Function(l) l.Quantity):N0} unit(s) added to stock, {AppInfo.Money(saved.Total)}.")
            Else
                msg.Append($"Purchase order {saved.PONumber} saved — {AppInfo.Money(saved.Total)}.")
            End If
            If saved.PreviousBalance > 0 Then
                If saved.AppliedToPreviousBalance > 0 Then
                    msg.Append($" {AppInfo.Money(saved.AppliedToPreviousBalance)} of it cleared part of the previous balance.")
                End If
                msg.Append(If(saved.RemainingBalance > 0,
                    $" We still owe {AppInfo.Money(saved.RemainingBalance)} in total.",
                    " The account with this supplier is now fully settled."))
            End If
            AppUI.Toast(msg.ToString(), AppUI.ToastKind.Success)
            Me.DialogResult = DialogResult.OK
            Me.Close()
        Catch ex As Exception
            MessageBox.Show("Could not save purchase order: " & ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

End Class
