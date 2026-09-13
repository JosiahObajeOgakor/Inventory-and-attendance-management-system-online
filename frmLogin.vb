Imports System.Windows.Forms
Imports System.Drawing
Imports System.Runtime.InteropServices

''' Sign-in. All credential checking, lockout and audit live in Auth; this form
''' just collects the username/password, shows the result, and handles a forced
''' first-sign-in password change before returning OK.
'''
''' Presented as a borderless white card floating on a neutral grey backdrop,
''' centred on screen — the window is dragged by its background/card (Win32
''' caption-drag trick) and closed with the "✕" glyph or Esc, since there's no
''' OS title bar.
Public Class frmLogin
    Inherits Form

    <DllImport("user32.dll")>
    Private Shared Function ReleaseCapture() As Boolean
    End Function

    <DllImport("user32.dll")>
    Private Shared Function SendMessage(hWnd As IntPtr, msg As Integer, wParam As Integer, lParam As Integer) As Integer
    End Function

    Private Const WM_NCLBUTTONDOWN As Integer = &HA1
    Private Const HT_CAPTION As Integer = &H2

    ''' Fixed neutral grey backdrop — deliberately not tied to Theme.Current, so
    ''' the sign-in card always looks the same regardless of the shop's chosen palette.
    Private Shared ReadOnly FloatingBg As Color = Color.FromArgb(231, 233, 236)

    Private ReadOnly card As New CardPanel() With {.CardColor = Color.White, .Size = New Size(420, 500)}
    Private ReadOnly lblClose As New Label() With {.Text = "✕", .AutoSize = True, .Cursor = Cursors.Hand, .Tag = "keepfont", .Font = New Font("Segoe UI", 11), .ForeColor = Color.FromArgb(120, 124, 130)}
    Private ReadOnly lblBrand As New Label() With {.AutoSize = True, .Tag = "heading", .Margin = New Padding(0, 4, 0, 0)}
    Private ReadOnly lblCaption As New Label() With {.AutoSize = True, .Tag = "keepfont", .ForeColor = Color.FromArgb(112, 118, 126), .Font = New Font("Segoe UI", 9.5F), .TextAlign = ContentAlignment.MiddleCenter, .MaximumSize = New Size(320, 0), .Anchor = AnchorStyles.None}
    Private ReadOnly lblUserCaption As New Label() With {.Text = "USERNAME", .AutoSize = True, .Tag = "keepfont", .ForeColor = Color.FromArgb(142, 148, 156), .Font = New Font("Segoe UI", 8, FontStyle.Bold), .Margin = New Padding(0, 0, 0, 4)}
    Private ReadOnly lblPassCaption As New Label() With {.Text = "PASSWORD", .AutoSize = True, .Tag = "keepfont", .ForeColor = Color.FromArgb(142, 148, 156), .Font = New Font("Segoe UI", 8, FontStyle.Bold), .Margin = New Padding(0, 0, 0, 4)}
    Private ReadOnly txtUsername As New TextBox() With {.BorderStyle = BorderStyle.FixedSingle, .Margin = New Padding(0, 0, 0, 16)}
    Private ReadOnly txtPassword As New TextBox() With {.UseSystemPasswordChar = True, .BorderStyle = BorderStyle.FixedSingle, .Margin = New Padding(0, 0, 0, 6)}
    Private ReadOnly lblError As New Label() With {.ForeColor = Color.Firebrick, .AutoSize = True, .Tag = "keepfont", .MaximumSize = New Size(320, 0), .Margin = New Padding(0, 4, 0, 4)}
    Private ReadOnly btnLogin As New Button() With {.Text = "Sign in", .Tag = "primary", .Height = 42, .Margin = New Padding(0, 10, 0, 18)}

    Public Property LoggedInUserID As Integer
    Public Property LoggedInFullName As String
    Public Property LoggedInRole As String

    Public Sub New()
        Text = Theme.AppName & " — Sign in"
        FormBorderStyle = FormBorderStyle.None
        StartPosition = FormStartPosition.CenterScreen
        Width = 560
        Height = 680
        ShowInTaskbar = True
        KeyPreview = True

        BuildCard()
        Controls.Add(card)
        lblClose.Location = New Point(Width - lblClose.Width - 24, 20)
        Controls.Add(lblClose)

        AddHandler Load, Sub(s, e)
                             CenterCard()
                             txtUsername.Focus()
                         End Sub
        AddHandler Resize, Sub(s, e) CenterCard()
        AddHandler lblClose.Click, Sub(s, e) CancelAndClose()
        AddHandler KeyDown, Sub(s, e) If e.KeyCode = Keys.Escape Then CancelAndClose()
        AddHandler MouseDown, AddressOf DragForm
        AddHandler card.MouseDown, AddressOf DragForm
        AddHandler btnLogin.Click, AddressOf btnLogin_Click
        AddHandler txtPassword.KeyDown, Sub(s, e) If e.KeyCode = Keys.Enter Then btnLogin_Click(s, e)
        Me.AcceptButton = btnLogin

        Theme.Apply(Me)
        BackColor = FloatingBg   ' the sign-in backdrop stays neutral grey regardless of the active palette
    End Sub

    ''' No OS title bar to drag by, so clicking the backdrop or card moves the
    ''' window the same way an OS caption bar would (standard Win32 trick).
    Private Sub DragForm(sender As Object, e As MouseEventArgs)
        If e.Button = MouseButtons.Left Then
            ReleaseCapture()
            SendMessage(Me.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0)
        End If
    End Sub

    Private Sub CancelAndClose()
        Me.DialogResult = DialogResult.Cancel
        Me.Close()
    End Sub

    Private Sub CenterCard()
        card.Location = New Point((ClientSize.Width - card.Width) \ 2, (ClientSize.Height - card.Height) \ 2)
    End Sub

    ''' Logo + app name, tagline, then the username/password fields stacked
    ''' full-width above the Sign in button — one clean white surface instead
    ''' of the old two-tone header-band dialog.
    Private Sub BuildCard()
        Dim table As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .ColumnCount = 1, .Padding = New Padding(36, 34, 36, 26)}
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        AddHandler table.MouseDown, AddressOf DragForm   ' the table (not the card) actually receives clicks on the blank card surface

        lblBrand.Text = Theme.AppName
        lblCaption.Text = $"Capture sensitive data and keep records clean on {Theme.AppName}."
        lblCaption.Margin = New Padding(0, 10, 0, 28)

        Dim brandRow As New FlowLayoutPanel() With {.AutoSize = True, .FlowDirection = FlowDirection.LeftToRight, .WrapContents = False, .Anchor = AnchorStyles.None}
        If Theme.Logo IsNot Nothing Then
            brandRow.Controls.Add(New PictureBox() With {.Image = Theme.Logo, .SizeMode = PictureBoxSizeMode.Zoom, .Size = New Size(40, 40), .Margin = New Padding(0, 0, 10, 0)})
        End If
        brandRow.Controls.Add(lblBrand)

        txtUsername.Dock = DockStyle.Fill
        txtPassword.Dock = DockStyle.Fill
        btnLogin.Dock = DockStyle.Fill

        Dim langRow As New FlowLayoutPanel() With {.AutoSize = True, .FlowDirection = FlowDirection.LeftToRight, .Anchor = AnchorStyles.None}
        langRow.Controls.Add(New Label() With {.Text = "Language:", .AutoSize = True, .Tag = "keepfont", .ForeColor = Color.FromArgb(142, 148, 156), .Font = New Font("Segoe UI", 8.5F), .Margin = New Padding(0, 6, 8, 0)})
        langRow.Controls.Add(UiHelpers.LanguagePicker(Sub() Theme.Apply(Me)))

        Dim rows As Control() = {brandRow, lblCaption, lblUserCaption, txtUsername, lblPassCaption, txtPassword, lblError, btnLogin, langRow}
        For i = 0 To rows.Length - 1
            table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            table.Controls.Add(rows(i), 0, i)
        Next

        card.Controls.Add(table)
    End Sub

    Private Sub btnLogin_Click(sender As Object, e As EventArgs)
        Dim username = txtUsername.Text.Trim()
        If String.IsNullOrEmpty(username) OrElse String.IsNullOrEmpty(txtPassword.Text) Then
            lblError.Text = "Enter username and password."
            Return
        End If

        Dim r = Auth.TryLogin(username, txtPassword.Text)
        Select Case r.Outcome
            Case Auth.LoginOutcome.Success
                Finish(r)

            Case Auth.LoginOutcome.MustChangePassword
                Using f As New frmSetPassword(r.UserID, requireOld:=False,
                                              title:="Set a password for " & r.FullName)
                    If f.ShowDialog(Me) = DialogResult.OK Then
                        Finish(r)
                    Else
                        lblError.Text = "You must set a password to sign in."
                    End If
                End Using

            Case Else
                lblError.Text = r.Message
                txtPassword.SelectAll()
                txtPassword.Focus()
        End Select
    End Sub

    Private Sub Finish(r As Auth.LoginResult)
        LoggedInUserID = r.UserID
        LoggedInFullName = r.FullName
        LoggedInRole = r.Role
        Me.DialogResult = DialogResult.OK
        Me.Close()
    End Sub

End Class
