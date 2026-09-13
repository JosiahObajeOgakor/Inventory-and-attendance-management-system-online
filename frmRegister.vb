Imports System.Windows.Forms

''' Create a user account. Two modes:
'''   • firstAdmin = True  — the one-time "set up the first administrator" screen
'''     shown by Program when the database has no usable admin. Role is forced to
'''     Admin and Cancel exits the app.
'''   • firstAdmin = False — an admin adding a colleague from the main window.
Public Class frmRegister
    Inherits Form

    Private ReadOnly _firstAdmin As Boolean
    Private ReadOnly txtFullName As New TextBox()
    Private ReadOnly txtUsername As New TextBox()
    Private ReadOnly txtPassword As New TextBox() With {.UseSystemPasswordChar = True}
    Private ReadOnly txtConfirm As New TextBox() With {.UseSystemPasswordChar = True}
    Private ReadOnly cboRole As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly lblHint As New Label() With {.AutoSize = True, .Tag = "keepfont"}

    Public ReadOnly Property NewUserId As Integer

    Public Sub New(Optional firstAdmin As Boolean = False)
        _firstAdmin = firstAdmin
        Text = If(firstAdmin, Theme.AppName & " — create the first administrator", "Add user account")
        Width = 430
        Height = 400
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterScreen
        MinimizeBox = False : MaximizeBox = False

        cboRole.Items.AddRange({"Admin", "Warehouse Clerk"})
        cboRole.SelectedIndex = 0
        cboRole.Enabled = Not firstAdmin

        Dim t = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(t, "Full name", txtFullName)
        UiHelpers.AddLabeled(t, "Username", txtUsername)
        UiHelpers.AddLabeled(t, "Password", txtPassword)
        UiHelpers.AddLabeled(t, "Confirm password", txtConfirm)
        UiHelpers.AddLabeled(t, "Role", cboRole)

        Dim hintHost As New Panel() With {.Dock = DockStyle.Top, .Height = 46, .Padding = New Padding(16, 4, 16, 4)}
        lblHint.Text = "Password: at least 8 characters, letters and numbers."
        lblHint.ForeColor = Theme.Current.TextMuted
        hintHost.Controls.Add(lblHint)

        If firstAdmin Then
            Dim intro As New Label() With {.Dock = DockStyle.Top, .Height = 44, .Tag = "keepfont",
                .Padding = New Padding(16, 10, 16, 0),
                .Text = "No administrator is set up yet. Create one now — you'll sign in with it."}
            Controls.Add(hintHost)
            Controls.Add(t)
            Controls.Add(intro)
        Else
            Controls.Add(hintHost)
            Controls.Add(t)
        End If

        UiHelpers.AddOkCancelRow(Me, If(firstAdmin, "Create administrator", "Create account"), AddressOf Save_Click)
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        ' A fresh install lets the Admin create ONLY their own account; adding
        ' more users (clerks or extra admins) needs an activated licence.
        If Not _firstAdmin AndAlso Not Licensing.IsActivated() Then
            Fail("Activate ChewyStock before adding more user accounts.")
            Return
        End If
        If String.IsNullOrWhiteSpace(txtFullName.Text) OrElse String.IsNullOrWhiteSpace(txtUsername.Text) Then
            Fail("Full name and username are required.")
            Return
        End If
        If txtUsername.Text.Trim().Contains(" ") Then
            Fail("Username can't contain spaces.")
            Return
        End If
        If txtPassword.Text <> txtConfirm.Text Then
            Fail("The two passwords don't match.")
            Return
        End If
        Dim problem = Security.PasswordProblem(txtPassword.Text)
        If problem IsNot Nothing Then
            Fail(problem)
            Return
        End If
        If Auth.UsernameTaken(txtUsername.Text) Then
            Fail("That username is already taken.")
            Return
        End If

        Try
            _NewUserId = Auth.CreateAccount(txtFullName.Text, txtUsername.Text, txtPassword.Text,
                                            If(_firstAdmin, "Admin", cboRole.Text))
            AppUI.Toast($"Account ""{txtUsername.Text.Trim()}"" created.", AppUI.ToastKind.Success)
            DialogResult = DialogResult.OK
            Close()
        Catch ex As Exception
            Fail("Could not create the account: " & ex.Message)
        End Try
    End Sub

    Private Sub Fail(msg As String)
        lblHint.Text = msg
        lblHint.ForeColor = Drawing.Color.Firebrick
    End Sub

End Class
