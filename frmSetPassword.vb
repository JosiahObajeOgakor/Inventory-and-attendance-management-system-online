Imports System.Windows.Forms

''' Set / change a password. Used for the forced first-sign-in change and for a
''' voluntary "Change my password" from the main window. When `requireOld` is
''' true the current password must be entered and verified first.
Public Class frmSetPassword
    Inherits Form

    Private ReadOnly _userId As Integer
    Private ReadOnly _requireOld As Boolean
    Private ReadOnly txtOld As New TextBox() With {.UseSystemPasswordChar = True}
    Private ReadOnly txtNew As New TextBox() With {.UseSystemPasswordChar = True}
    Private ReadOnly txtConfirm As New TextBox() With {.UseSystemPasswordChar = True}
    Private ReadOnly lblHint As New Label() With {.ForeColor = Drawing.Color.Firebrick, .AutoSize = True, .Tag = "keepfont"}

    Public Sub New(userId As Integer, Optional requireOld As Boolean = True, Optional title As String = "Set your password")
        _userId = userId
        _requireOld = requireOld
        Text = title
        Width = 400
        Height = 320
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        Dim t = UiHelpers.NewFormTable()
        If _requireOld Then UiHelpers.AddLabeled(t, "Current password", txtOld)
        UiHelpers.AddLabeled(t, "New password", txtNew)
        UiHelpers.AddLabeled(t, "Confirm new", txtConfirm)

        Dim hintHost As New Panel() With {.Dock = DockStyle.Top, .Height = 44, .Padding = New Padding(16, 4, 16, 4)}
        lblHint.Text = "At least 8 characters, with letters and numbers."
        lblHint.ForeColor = Theme.Current.TextMuted
        hintHost.Controls.Add(lblHint)

        Controls.Add(hintHost)
        Controls.Add(t)
        UiHelpers.AddOkCancelRow(Me, "Save password", AddressOf Save_Click)
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        If _requireOld Then
            Dim stored = Auth.StoredHash(_userId)
            If Security.IsRealHash(stored) AndAlso Not Security.VerifyPassword(txtOld.Text, stored) Then
                Fail("Current password is wrong.")
                Return
            End If
        End If
        If txtNew.Text <> txtConfirm.Text Then
            Fail("The two new passwords don't match.")
            Return
        End If
        Dim problem = Security.PasswordProblem(txtNew.Text)
        If problem IsNot Nothing Then
            Fail(problem)
            Return
        End If
        Auth.SetPassword(_userId, txtNew.Text)
        AppUI.Toast("Password updated.", AppUI.ToastKind.Success)
        DialogResult = DialogResult.OK
        Close()
    End Sub

    Private Sub Fail(msg As String)
        lblHint.Text = msg
        lblHint.ForeColor = Drawing.Color.Firebrick
    End Sub

End Class
