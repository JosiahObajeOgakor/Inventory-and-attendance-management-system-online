Imports System.Windows.Forms

''' Add / edit a supplier with full contact details: address, phone, email,
''' Tax ID. Used from the Suppliers screen and inline from New Purchase.
Public Class frmAddSupplier
    Inherits Form

    Private ReadOnly _editId As Integer?
    Private txtName As New TextBox()
    Private txtCategory As New TextBox()
    Private txtContact As New TextBox()
    Private txtPhone As New TextBox()
    Private txtEmail As New TextBox()
    Private txtAddress As New TextBox()
    Private txtTaxID As New TextBox()

    Public ReadOnly Property SupplierName As String
        Get
            Return txtName.Text.Trim()
        End Get
    End Property

    Public Sub New(Optional editSupplierId As Integer? = Nothing)
        _editId = editSupplierId
        Text = If(_editId.HasValue, "Edit supplier", "Add supplier")
        Width = 420
        Height = 430
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False

        UiHelpers.AddOkCancelRow(Me, "Save supplier", AddressOf Save_Click)
        Dim t = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(t, "Name", txtName)
        UiHelpers.AddLabeled(t, "Supplies (category)", txtCategory)
        UiHelpers.AddLabeled(t, "Contact person", txtContact)
        UiHelpers.AddLabeled(t, "Phone", txtPhone)
        UiHelpers.AddLabeled(t, "Email", txtEmail)
        UiHelpers.AddLabeled(t, "Address", txtAddress)
        UiHelpers.AddLabeled(t, "Tax ID", txtTaxID)
        Controls.Add(t)

        If _editId.HasValue Then
            Dim r = DataAccess.GetTable(
                "SELECT Name, Category, ContactName, Phone, Email, [Address], TaxID FROM Suppliers WHERE SupplierID = @id",
                New Dictionary(Of String, Object) From {{"@id", _editId.Value}}).Rows(0)
            txtName.Text = Convert.ToString(r("Name"))
            txtCategory.Text = Convert.ToString(r("Category"))
            txtContact.Text = Convert.ToString(r("ContactName"))
            txtPhone.Text = Convert.ToString(r("Phone"))
            txtEmail.Text = Convert.ToString(r("Email"))
            txtAddress.Text = Convert.ToString(r("Address"))
            txtTaxID.Text = Convert.ToString(r("TaxID"))
        End If
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        If String.IsNullOrWhiteSpace(SupplierName) Then
            AppUI.Info(Me, "Supplier name is required.")
            Return
        End If
        Dim p = New Dictionary(Of String, Object) From {
            {"@n", SupplierName}, {"@cat", Blank(txtCategory)}, {"@c", Blank(txtContact)},
            {"@p", Blank(txtPhone)}, {"@e", Blank(txtEmail)}, {"@a", Blank(txtAddress)}, {"@x", Blank(txtTaxID)}}
        If _editId.HasValue Then
            p("@id") = _editId.Value
            DataAccess.Execute("UPDATE Suppliers SET Name=@n, Category=@cat, ContactName=@c, Phone=@p, Email=@e, [Address]=@a, TaxID=@x WHERE SupplierID=@id", p)
        Else
            DataAccess.Execute("INSERT INTO Suppliers (Name, Category, ContactName, Phone, Email, [Address], TaxID) VALUES (@n,@cat,@c,@p,@e,@a,@x)", p)
        End If
        DialogResult = DialogResult.OK
        Close()
    End Sub

    Private Shared Function Blank(tb As TextBox) As Object
        Return If(String.IsNullOrWhiteSpace(tb.Text), CObj(DBNull.Value), tb.Text.Trim())
    End Function

End Class
