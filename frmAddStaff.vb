Imports System.Windows.Forms

''' Add-staff dialog. Password hashing happens in the caller so this dialog
''' never stores plain text itself.
Public Class frmAddStaff
    Inherits Form

    Private txtFullName As New TextBox()
    Private txtUsername As New TextBox()
    Private txtPassword As New TextBox() With {.PasswordChar = "*"c}
    Private cboRole As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}

    Public ReadOnly Property FullName As String
        Get
            Return txtFullName.Text.Trim()
        End Get
    End Property
    Public ReadOnly Property Username As String
        Get
            Return txtUsername.Text.Trim()
        End Get
    End Property
    Public ReadOnly Property Role As String
        Get
            Return cboRole.Text
        End Get
    End Property
    ''' Real build: return BCrypt.Net.BCrypt.HashPassword(txtPassword.Text) here.
    Public ReadOnly Property HashedPassword As String
        Get
            Return "HASH_ME:" & txtPassword.Text
        End Get
    End Property

    Public Sub New()
        Text = "Add staff account"
        Width = 380
        Height = 320
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False

        cboRole.Items.AddRange({"Admin", "Warehouse Clerk"})
        cboRole.SelectedIndex = 1

        UiHelpers.AddOkCancelRow(Me, "Save account", AddressOf btnSave_Click)
        Dim table = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(table, "Full name", txtFullName)
        UiHelpers.AddLabeled(table, "Username", txtUsername)
        UiHelpers.AddLabeled(table, "Temporary password", txtPassword)
        UiHelpers.AddLabeled(table, "Role", cboRole)
        Controls.Add(table)
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub btnSave_Click(sender As Object, e As EventArgs)
        If String.IsNullOrWhiteSpace(FullName) OrElse String.IsNullOrWhiteSpace(Username) OrElse txtPassword.Text.Length = 0 Then
            MessageBox.Show("All fields are required.", "Missing fields", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        Me.DialogResult = DialogResult.OK
        Me.Close()
    End Sub

End Class
