Imports System.Windows.Forms
Imports System.Data

''' Record goods produced. Adds the quantity to the chosen warehouse's stock and
''' logs it as production on the date given, so the inventory total goes up and
''' the Production history shows what was made and when. Any signed-in user can
''' record production.
Public Class frmProduction
    Inherits Form

    Private ReadOnly _userId As Integer
    Private cboProduct As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private cboWarehouse As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private numQty As New NumericUpDown() With {.Minimum = 1, .Maximum = 1000000, .Value = 1}
    Private dtpDate As New DateTimePicker() With {.Format = DateTimePickerFormat.Custom, .CustomFormat = "dd MMM yyyy"}
    Private txtBatch As New TextBox()
    Private chkExpiry As New CheckBox() With {.Text = "Set an expiry date", .AutoSize = True}
    Private dtpExpiry As New DateTimePicker() With {.Format = DateTimePickerFormat.Custom, .CustomFormat = "dd MMM yyyy", .Enabled = False}
    Private lblCurrent As New Label() With {.AutoSize = True, .Tag = "keepfont"}

    Public Sub New(userId As Integer)
        _userId = userId
        Text = "Record production"
        Width = 560
        Height = 560
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        cboProduct.DataSource = DataAccess.GetTable("SELECT ProductID, Name, Unit FROM Products WHERE IsActive = 1 ORDER BY Name")
        cboProduct.DisplayMember = "Name"
        cboProduct.ValueMember = "ProductID"

        cboWarehouse.DataSource = DataAccess.GetTable("SELECT WarehouseID, Name FROM Warehouses ORDER BY Name")
        cboWarehouse.DisplayMember = "Name"
        cboWarehouse.ValueMember = "WarehouseID"

        dtpDate.MaxDate = Date.Today
        dtpExpiry.MinDate = Date.Today
        dtpExpiry.Value = Date.Today.AddMonths(12)

        Dim t = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(t, "Product", cboProduct)
        UiHelpers.AddLabeled(t, "Into warehouse", cboWarehouse)
        UiHelpers.AddLabeled(t, "Quantity produced", numQty)
        UiHelpers.AddLabeled(t, "Produced on", dtpDate)
        UiHelpers.AddLabeled(t, "Batch / lot no.", txtBatch)
        UiHelpers.AddLabeled(t, "Expiry", chkExpiry)
        UiHelpers.AddLabeled(t, "Expiry date", dtpExpiry)

        Dim head As New Label() With {.Dock = DockStyle.Top, .AutoSize = True, .Tag = "keepfont", .MaximumSize = New Drawing.Size(500, 0),
            .Padding = New Padding(16, 10, 16, 8),
            .Text = "Adds to what is already in the warehouse, and keeps the production date in the history."}
        Dim foot As New Panel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 0, 16, 8)}
        foot.Controls.Add(lblCurrent)

        Controls.Add(foot)
        Controls.Add(t)
        Controls.Add(head)
        UiHelpers.AddOkCancelRow(Me, "Add to inventory", AddressOf Save_Click)

        AddHandler chkExpiry.CheckedChanged, Sub(s, e) dtpExpiry.Enabled = chkExpiry.Checked
        AddHandler cboProduct.SelectedIndexChanged, Sub(s, e) ShowCurrentStock()
        AddHandler cboWarehouse.SelectedIndexChanged, Sub(s, e) ShowCurrentStock()
        AddHandler Load, Sub(s, e) ShowCurrentStock()

        txtBatch.Text = "PROD-" & Date.Today.ToString("yyMMdd")
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub ShowCurrentStock()
        If cboProduct.SelectedValue Is Nothing OrElse cboWarehouse.SelectedValue Is Nothing Then Return
        Try
            Dim qty = Stock.QuantityInWarehouse(CInt(cboProduct.SelectedValue), CInt(cboWarehouse.SelectedValue))
            Dim unit = Convert.ToString(CType(cboProduct.SelectedItem, DataRowView)("Unit"))
            lblCurrent.Text = $"In this warehouse now: {qty:#,0} {unit.ToLowerInvariant()}(s)"
        Catch
            lblCurrent.Text = ""
        End Try
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        If cboProduct.SelectedValue Is Nothing OrElse cboWarehouse.SelectedValue Is Nothing Then Return
        Dim qty = CInt(numQty.Value)
        Dim producedOn = dtpDate.Value.Date
        Dim err = Stock.RecordProduction(CInt(cboProduct.SelectedValue), CInt(cboWarehouse.SelectedValue), qty,
                                         producedOn, txtBatch.Text,
                                         If(chkExpiry.Checked, CType(dtpExpiry.Value.Date, Date?), Nothing), _userId)
        If err <> "" Then
            AppUI.Toast("Could not record production: " & err, AppUI.ToastKind.Error)
            Return
        End If
        AppUI.Toast($"Added {qty:#,0} to stock — produced {producedOn:dd MMM yyyy}.", AppUI.ToastKind.Success)
        Anim.SuccessTick(If(Owner, Me), "Production added")
        DialogResult = DialogResult.OK
        Close()
    End Sub

End Class
