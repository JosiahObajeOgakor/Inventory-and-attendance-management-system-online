Imports System.Windows.Forms

''' New / edit customer dialog. Used from the Customers screen and inline from
''' New Sale ("+ New customer"). Captures ranking (Distributor / Wholesaler /
''' Retailer / Walk-in), Tax ID, address and the per-customer rebate rate.
Public Class frmAddCustomer
    Inherits Form

    Private ReadOnly _editId As Integer?
    Private txtName As New TextBox()
    Private cboType As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private txtContact As New TextBox()
    Private txtPhone As New TextBox()
    Private txtEmail As New TextBox()
    Private txtAddress As New TextBox()
    Private txtLocation As New TextBox()
    Private txtTaxID As New TextBox()
    Private numRebate As New NumericUpDown() With {.Maximum = 100, .DecimalPlaces = 2, .Value = 1D}
    Private numCreditLimit As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}

    Public ReadOnly Property CustomerName As String
        Get
            Return txtName.Text.Trim()
        End Get
    End Property
    Public ReadOnly Property CustomerType As String
        Get
            Return cboType.Text
        End Get
    End Property
    Public ReadOnly Property ContactName As String
        Get
            Return txtContact.Text.Trim()
        End Get
    End Property
    Public ReadOnly Property Phone As String
        Get
            Return txtPhone.Text.Trim()
        End Get
    End Property
    Public ReadOnly Property Email As String
        Get
            Return txtEmail.Text.Trim()
        End Get
    End Property
    Public Shadows ReadOnly Property Location As String
        Get
            Return txtLocation.Text.Trim()
        End Get
    End Property
    Public ReadOnly Property Address As String
        Get
            Return txtAddress.Text.Trim()
        End Get
    End Property
    Public ReadOnly Property TaxID As String
        Get
            Return txtTaxID.Text.Trim()
        End Get
    End Property
    Public ReadOnly Property RebateRatePct As Decimal
        Get
            Return numRebate.Value
        End Get
    End Property
    Public ReadOnly Property CreditLimit As Decimal
        Get
            Return numCreditLimit.Value
        End Get
    End Property

    Public Sub New(Optional editCustomerId As Integer? = Nothing)
        _editId = editCustomerId
        Text = If(_editId.HasValue, "Edit customer", "Add customer")
        Width = 420
        Height = 520
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False

        cboType.Items.AddRange({"Distributor", "Wholesaler", "Retailer", "Walk-in"})
        cboType.SelectedIndex = 2

        UiHelpers.AddOkCancelRow(Me, "Save customer", AddressOf btnSave_Click)
        Dim table = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(table, "Name", txtName)
        UiHelpers.AddLabeled(table, "Ranking", cboType)
        UiHelpers.AddLabeled(table, "Contact person", txtContact)
        UiHelpers.AddLabeled(table, "Phone", txtPhone)
        UiHelpers.AddLabeled(table, "Email", txtEmail)
        UiHelpers.AddLabeled(table, "Address", txtAddress)
        UiHelpers.AddLabeled(table, "Location (city, state)", txtLocation)
        UiHelpers.AddLabeled(table, "Tax ID", txtTaxID)
        UiHelpers.AddLabeled(table, "Rebate rate %", numRebate)
        UiHelpers.AddLabeled(table, "Credit limit", numCreditLimit)
        Controls.Add(table)

        If _editId.HasValue Then LoadExisting()
        AddHandler cboType.SelectedIndexChanged, Sub(s, e)
                                                     If cboType.Text = "Walk-in" Then numRebate.Value = 0D
                                                 End Sub
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub LoadExisting()
        Dim r = DataAccess.GetTable(
            "SELECT Name, CustomerType, ContactName, Phone, Email, [Address], Location, TaxID, RebateRatePct, CreditLimit FROM Customers WHERE CustomerID = @id",
            New Dictionary(Of String, Object) From {{"@id", _editId.Value}}).Rows(0)
        txtName.Text = Convert.ToString(r("Name"))
        cboType.Text = Convert.ToString(r("CustomerType"))
        txtContact.Text = Convert.ToString(r("ContactName"))
        txtPhone.Text = Convert.ToString(r("Phone"))
        txtEmail.Text = Convert.ToString(r("Email"))
        txtAddress.Text = Convert.ToString(r("Address"))
        txtLocation.Text = Convert.ToString(r("Location"))
        txtTaxID.Text = Convert.ToString(r("TaxID"))
        numRebate.Value = Convert.ToDecimal(r("RebateRatePct"))
        numCreditLimit.Value = Convert.ToDecimal(r("CreditLimit"))
    End Sub

    ''' When opened as an editor, saves directly; otherwise the caller inserts.
    Public ReadOnly Property EditId As Integer?
        Get
            Return _editId
        End Get
    End Property

    Private Sub btnSave_Click(sender As Object, e As EventArgs)
        If String.IsNullOrWhiteSpace(CustomerName) Then
            AppUI.Info(Me, "Name is required.")
            Return
        End If
        If _editId.HasValue Then
            DataAccess.Execute(
                "UPDATE Customers SET Name=@n, CustomerType=@t, ContactName=@c, Phone=@p, Email=@e, [Address]=@a, Location=@l, TaxID=@x, RebateRatePct=@rb, CreditLimit=@cl WHERE CustomerID=@id",
                Params())
        End If
        Me.DialogResult = DialogResult.OK
        Me.Close()
    End Sub

    ''' Parameter set shared by the inline-insert caller and the editor.
    Public Function Params() As Dictionary(Of String, Object)
        Return New Dictionary(Of String, Object) From {
            {"@n", CustomerName}, {"@t", CustomerType}, {"@c", NullIfBlank(ContactName)},
            {"@p", NullIfBlank(Phone)}, {"@e", NullIfBlank(Email)}, {"@a", NullIfBlank(Address)},
            {"@l", NullIfBlank(Location)}, {"@x", NullIfBlank(TaxID)},
            {"@rb", RebateRatePct}, {"@cl", CreditLimit}, {"@id", If(_editId.HasValue, CObj(_editId.Value), DBNull.Value)}
        }
    End Function

    Private Shared Function NullIfBlank(s As String) As Object
        Return If(String.IsNullOrWhiteSpace(s), CObj(DBNull.Value), s)
    End Function

End Class
