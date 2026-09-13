Imports System.Windows.Forms

''' Add-item dialog — built with UiHelpers so this is a plain field list.
Public Class frmAddItem
    Inherits Form

    Private txtSku As New TextBox()
    Private txtName As New TextBox()
    Private cboCategory As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private cboWarehouse As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private txtBatch As New TextBox()
    Private dtpExpiry As New DateTimePicker() With {.Format = DateTimePickerFormat.Short}
    Private numQty As New NumericUpDown() With {.Maximum = 1000000}
    Private cboUnit As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private numReorderLevel As New NumericUpDown() With {.Maximum = 100000}
    Private numCost As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private numPriceRetail As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private numPriceWholesaler As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private numPriceDistributor As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}

    Public ReadOnly Property Sku As String
        Get
            Return txtSku.Text.Trim()
        End Get
    End Property
    Public Shadows ReadOnly Property ProductName As String
        Get
            Return txtName.Text.Trim()
        End Get
    End Property
    Public ReadOnly Property CategoryID As Integer
        Get
            Return CInt(cboCategory.SelectedValue)
        End Get
    End Property
    Public ReadOnly Property WarehouseID As Integer
        Get
            Return CInt(cboWarehouse.SelectedValue)
        End Get
    End Property
    Public ReadOnly Property BatchNumber As String
        Get
            Return txtBatch.Text.Trim()
        End Get
    End Property
    Public ReadOnly Property ExpiryDate As Date
        Get
            Return dtpExpiry.Value.Date
        End Get
    End Property
    Public ReadOnly Property Quantity As Integer
        Get
            Return CInt(numQty.Value)
        End Get
    End Property
    Public ReadOnly Property Unit As String
        Get
            Return cboUnit.Text
        End Get
    End Property
    Public ReadOnly Property ReorderLevel As Integer
        Get
            Return CInt(numReorderLevel.Value)
        End Get
    End Property
    Public ReadOnly Property CostPrice As Decimal
        Get
            Return numCost.Value
        End Get
    End Property
    Public ReadOnly Property PriceRetail As Decimal
        Get
            Return numPriceRetail.Value
        End Get
    End Property
    Public ReadOnly Property PriceWholesaler As Decimal
        Get
            Return If(numPriceWholesaler.Value > 0, numPriceWholesaler.Value, numPriceRetail.Value)
        End Get
    End Property
    Public ReadOnly Property PriceDistributor As Decimal
        Get
            Return If(numPriceDistributor.Value > 0, numPriceDistributor.Value, numPriceRetail.Value)
        End Get
    End Property
    ''' Legacy single price — kept in sync with the retail tier.
    Public ReadOnly Property SellingPrice As Decimal
        Get
            Return numPriceRetail.Value
        End Get
    End Property

    Public Sub New()
        Text = "Add inventory item"
        Width = 460
        Height = 600
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False

        cboCategory.DataSource = DataAccess.GetTable("SELECT CategoryID, Name FROM Categories ORDER BY Name")
        cboCategory.DisplayMember = "Name"
        cboCategory.ValueMember = "CategoryID"

        cboWarehouse.DataSource = DataAccess.GetTable("SELECT WarehouseID, Name FROM Warehouses ORDER BY Name")
        cboWarehouse.DisplayMember = "Name"
        cboWarehouse.ValueMember = "WarehouseID"

        cboUnit.Items.AddRange({"Bag", "Carton", "Piece", "Kg"})
        cboUnit.SelectedIndex = 0

        AddOkCancelButtons()

        Dim table = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(table, "SKU", txtSku)
        UiHelpers.AddLabeled(table, "Product name", txtName)
        UiHelpers.AddLabeled(table, "Category", cboCategory)
        UiHelpers.AddLabeled(table, "Warehouse", cboWarehouse)
        UiHelpers.AddLabeled(table, "Batch #", txtBatch)
        UiHelpers.AddLabeled(table, "Expiry date", dtpExpiry)
        UiHelpers.AddLabeled(table, "Quantity", numQty)
        UiHelpers.AddLabeled(table, "Unit", cboUnit)
        UiHelpers.AddLabeled(table, "Reorder level", numReorderLevel)
        UiHelpers.AddLabeled(table, "Cost price", numCost)
        UiHelpers.AddLabeled(table, "Retail price", numPriceRetail)
        UiHelpers.AddLabeled(table, "Wholesaler price", numPriceWholesaler)
        UiHelpers.AddLabeled(table, "Distributor price", numPriceDistributor)
        Controls.Add(table)
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub AddOkCancelButtons()
        UiHelpers.AddOkCancelRow(Me, "Save item", AddressOf btnSave_Click)
    End Sub

    Private Sub btnSave_Click(sender As Object, e As EventArgs)
        If String.IsNullOrWhiteSpace(Sku) OrElse String.IsNullOrWhiteSpace(ProductName) Then
            MessageBox.Show("SKU and product name are required.", "Missing fields", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        Me.DialogResult = DialogResult.OK
        Me.Close()
    End Sub

End Class
