Imports System.Windows.Forms
Imports System.Data

''' New purchase order: pick supplier, add product/qty/cost lines, optionally add
''' VAT (off by default), save header + lines + a Ledger credit entry (we now owe
''' the supplier more).
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
    Private btnNewSupplier As New Button() With {.Text = "+ New", .AutoSize = True}
    Private gridLines As DataGridView = UiHelpers.NewGrid()
    Private chkVat As New CheckBox() With {.AutoSize = True, .Checked = False, .Margin = New Padding(0, 4, 24, 0)}
    Private lblVat As New Label() With {.AutoSize = True, .Tag = "keepfont", .Margin = New Padding(0, 6, 24, 0)}
    Private lblTotal As New Label() With {.AutoSize = True, .Font = New Drawing.Font("Segoe UI", 12, Drawing.FontStyle.Bold)}

    Private lineItems As New DataTable()
    Private ReadOnly ConfiguredVatRate As Decimal = AppInfo.VatRate

    Public Sub New(userId As Integer)
        currentUserId = userId
        Text = "New purchase order"
        Width = 700
        Height = 580
        StartPosition = FormStartPosition.CenterParent

        cboSupplier.DataSource = DataAccess.GetTable("SELECT SupplierID, Name FROM Suppliers ORDER BY Name")
        cboSupplier.DisplayMember = "Name"
        cboSupplier.ValueMember = "SupplierID"

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
        AddHandler btnNewSupplier.Click, Sub(s, e)
                                             Using f As New frmAddSupplier()
                                                 If f.ShowDialog() = DialogResult.OK Then
                                                     cboSupplier.DataSource = DataAccess.GetTable("SELECT SupplierID, Name FROM Suppliers ORDER BY Name")
                                                     cboSupplier.DisplayMember = "Name"
                                                     cboSupplier.ValueMember = "SupplierID"
                                                     cboSupplier.SelectedIndex = cboSupplier.FindStringExact(f.SupplierName)
                                                 End If
                                             End Using
                                         End Sub

        Dim lineRow As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 4, 16, 4)}
        lineRow.Controls.Add(New Label() With {.Text = "Product:", .AutoSize = True, .Margin = New Padding(0, 8, 8, 0)})
        lineRow.Controls.Add(cboProduct)
        lineRow.Controls.Add(New Label() With {.Text = "Qty:", .AutoSize = True, .Margin = New Padding(12, 8, 8, 0)})
        lineRow.Controls.Add(numQty)
        lineRow.Controls.Add(New Label() With {.Text = "Unit cost:", .AutoSize = True, .Margin = New Padding(12, 8, 8, 0)})
        lineRow.Controls.Add(numUnitCost)
        lineRow.Controls.Add(btnAddLine)

        chkVat.Text = $"Add VAT ({ConfiguredVatRate:0.##}%)"
        Dim totalRow As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .Padding = New Padding(16)}
        totalRow.Controls.Add(chkVat)
        totalRow.Controls.Add(lblVat)
        totalRow.Controls.Add(New Label() With {.Text = "Total:", .AutoSize = True, .Margin = New Padding(0, 6, 4, 0)})
        totalRow.Controls.Add(lblTotal)

        UiHelpers.AddOkCancelRow(Me, "Save purchase order", AddressOf btnSave_Click)
        Controls.Add(totalRow)
        Controls.Add(gridLines)
        Controls.Add(lineRow)
        Controls.Add(supplierRow)
        ' Dock order: Save/Cancel at the very bottom, total row above it, grid fills the rest.
        totalRow.BringToFront()
        gridLines.BringToFront()
        AddHandler gridLines.DataBindingComplete, Sub(s, e)
                                                      If gridLines.Columns.Contains("ProductID") Then gridLines.Columns("ProductID").Visible = False
                                                  End Sub

        AddHandler btnAddLine.Click, AddressOf btnAddLine_Click
        AddHandler chkVat.CheckedChanged, Sub(s, e) RecalcTotal()
        RecalcTotal()
        UiHelpers.FitToScreen(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub btnAddLine_Click(sender As Object, e As EventArgs)
        Dim row = CType(cboProduct.SelectedItem, DataRowView)
        If row Is Nothing Then Return
        lineItems.Rows.Add(CInt(row("ProductID")), row("Name").ToString(), CInt(numQty.Value), numUnitCost.Value)
        RecalcTotal()
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
        lblTotal.Text = AppInfo.Money(Subtotal() + VatAmount())
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
            .VatRate = If(chkVat.Checked, ConfiguredVatRate, 0D)}
        For Each r As DataRow In lineItems.Rows
            req.Lines.Add(New Purchasing.PurchaseLine() With {
                .ProductID = CInt(r("ProductID")),
                .ProductName = Convert.ToString(r("ProductName")),
                .Quantity = CInt(r("Qty")),
                .UnitCost = CDec(r("UnitCost"))})
        Next

        Try
            ' One transaction: header, lines and the ledger entry save together.
            Dim saved = Purchasing.Save(req, currentUserId)
            AppUI.Toast($"Purchase order {saved.PONumber} saved — {AppInfo.Money(saved.Total)}.", AppUI.ToastKind.Success)
            Me.DialogResult = DialogResult.OK
            Me.Close()
        Catch ex As Exception
            MessageBox.Show("Could not save purchase order: " & ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

End Class
