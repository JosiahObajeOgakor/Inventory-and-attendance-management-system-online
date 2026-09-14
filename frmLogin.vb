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

    Private ReadOnly card As New CardPanel() With {.CardColor = Color.White, .Size = New Size(420, 590)}
    Private ReadOnly lblClose As New Label() With {.Text = "✕", .AutoSize = True, .Cursor = Cursors.Hand, .Tag = "keepfont", .Font = New Font("Segoe UI", 11), .ForeColor = Color.FromArgb(120, 124, 130)}
    Private ReadOnly lblBrand As New Label() With {.AutoSize = True, .Tag = "heading", .Margin = New Padding(0, 4, 0, 0)}
    Private ReadOnly lblCaption As New Label() With {.AutoSize = True, .Tag = "keepfont", .ForeColor = Color.FromArgb(112, 118, 126), .Font = New Font("Segoe UI", 9.5F), .TextAlign = ContentAlignment.MiddleCenter, .MaximumSize = New Size(320, 0), .Anchor = AnchorStyles.None}
    Private ReadOnly lblUserCaption As New Label() With {.Text = "USERNAME", .AutoSize = True, .Tag = "keepfont", .ForeColor = Color.FromArgb(142, 148, 156), .Font = New Font("Segoe UI", 8, FontStyle.Bold), .Margin = New Padding(0, 0, 0, 4)}
    Private ReadOnly lblPassCaption As New Label() With {.Text = "PASSWORD", .AutoSize = True, .Tag = "keepfont", .ForeColor = Color.FromArgb(142, 148, 156), .Font = New Font("Segoe UI", 8, FontStyle.Bold), .Margin = New Padding(0, 0, 0, 4)}
    Private ReadOnly txtUsername As New TextBox() With {.BorderStyle = BorderStyle.FixedSingle, .Margin = New Padding(0, 0, 0, 16)}
    Private ReadOnly txtPassword As New TextBox() With {.UseSystemPasswordChar = True, .BorderStyle = BorderStyle.FixedSingle, .Margin = New Padding(0, 0, 0, 6)}
    Private ReadOnly lblError As New Label() With {.ForeColor = Color.Firebrick, .AutoSize = True, .Tag = "keepfont", .MaximumSize = New Size(320, 0), .Margin = New Padding(0, 4, 0, 4)}
    Private ReadOnly btnLogin As New Button() With {.Text = "Sign in", .Tag = "primary", .Height = 42, .Margin = New Padding(0, 10, 0, 18)}

    ''' Which business to sign into. Every account can open either one.
    Private ReadOnly lblCompanyCaption As New Label() With {.Text = "SIGN IN TO", .AutoSize = True, .Tag = "keepfont", .ForeColor = Color.FromArgb(142, 148, 156), .Font = New Font("Segoe UI", 8, FontStyle.Bold), .Margin = New Padding(0, 0, 0, 4)}
    Private ReadOnly companyRow As New TableLayoutPanel() With {.ColumnCount = 2, .RowCount = 1, .Dock = DockStyle.Fill, .AutoSize = True, .Margin = New Padding(0, 0, 0, 16)}
    Private ReadOnly companyChoices As New Dictionary(Of Company, RadioButton)
    Private ReadOnly picBrand As New PictureBox() With {.SizeMode = PictureBoxSizeMode.Zoom, .Size = New Size(48, 48), .Margin = New Padding(0, 0, 10, 0)}
    Private _company As Company = Company.LastUsed()

    Public Property LoggedInUserID As Integer
    Public Property LoggedInFullName As String
    Public Property LoggedInRole As String
    Public ReadOnly Property SelectedCompany As Company
        Get
            Return _company
        End Get
    End Property

    Public Sub New()
        ' Sign-in always starts from the home database, where accounts live —
        ' even when the previous session (before a sign-out) was the other business.
        Company.Use(Company.Home)
        Text = Theme.AppName & " — Sign in"
        FormBorderStyle = FormBorderStyle.None
        StartPosition = FormStartPosition.CenterScreen
        Width = 560
        Height = 770
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
        lblCaption.Text = $"Choose the business, then sign in. One account works for both."
        lblCaption.Margin = New Padding(0, 10, 0, 22)
        BuildCompanyPicker()

        Dim brandRow As New FlowLayoutPanel() With {.AutoSize = True, .FlowDirection = FlowDirection.LeftToRight, .WrapContents = False, .Anchor = AnchorStyles.None}
        brandRow.Controls.Add(picBrand)
        brandRow.Controls.Add(lblBrand)
        ShowBrand()

        txtUsername.Dock = DockStyle.Fill
        txtPassword.Dock = DockStyle.Fill
        btnLogin.Dock = DockStyle.Fill

        Dim langRow As New FlowLayoutPanel() With {.AutoSize = True, .FlowDirection = FlowDirection.LeftToRight, .Anchor = AnchorStyles.None}
        langRow.Controls.Add(New Label() With {.Text = "Language:", .AutoSize = True, .Tag = "keepfont", .ForeColor = Color.FromArgb(142, 148, 156), .Font = New Font("Segoe UI", 8.5F), .Margin = New Padding(0, 6, 8, 0)})
        langRow.Controls.Add(UiHelpers.LanguagePicker(Sub() Theme.Apply(Me)))

        Dim rows As Control() = {brandRow, lblCaption, lblCompanyCaption, companyRow, lblUserCaption, txtUsername, lblPassCaption, txtPassword, lblError, btnLogin, langRow}
        For i = 0 To rows.Length - 1
            table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            table.Controls.Add(rows(i), 0, i)
        Next

        card.Controls.Add(table)
    End Sub

    ''' Two big side-by-side choices rather than a dropdown: which business the
    ''' day's sales land in is the one thing on this screen that must not be
    ''' picked by accident, so both options are always in plain view.
    Private Sub BuildCompanyPicker()
        companyRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        companyRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        Dim column = 0
        For Each c In Company.All
            Dim choice As New RadioButton() With {
                .Text = c.DisplayName, .Appearance = Appearance.Button, .FlatStyle = FlatStyle.Flat,
                .TextAlign = ContentAlignment.MiddleCenter, .Dock = DockStyle.Fill, .Height = 50,
                .Tag = "keepfont", .Font = New Font("Segoe UI", 10, FontStyle.Bold), .Cursor = Cursors.Hand,
                .Margin = New Padding(If(column = 0, 0, 6), 0, If(column = 0, 6, 0), 0),
                .Checked = c Is _company}
            Dim picked = c
            AddHandler choice.CheckedChanged, Sub(s, e)
                                                  If choice.Checked Then _company = picked
                                                  StyleCompanyChoices()
                                              End Sub
            companyChoices(c) = choice
            companyRow.Controls.Add(choice, column, 0)
            column += 1
        Next
        StyleCompanyChoices()
    End Sub

    ''' The card wears the chosen business's logo and app name as soon as it's
    ''' picked. Only the look changes here — the app itself switches business
    ''' after a successful sign-in.
    Private Sub ShowBrand()
        picBrand.Image = _company.Logo
        picBrand.Visible = _company.Logo IsNot Nothing
        lblBrand.Text = _company.AppName
        Text = _company.AppName & " — Sign in"
    End Sub

    Private Sub StyleCompanyChoices()
        ShowBrand()
        For Each kv In companyChoices
            Dim b = kv.Value
            Dim on_ = b.Checked
            b.BackColor = If(on_, Theme.Current.Primary, Color.White)
            b.ForeColor = If(on_, Theme.Current.PrimaryFg, Color.FromArgb(60, 66, 74))
            b.FlatAppearance.BorderColor = If(on_, Theme.Current.Primary, Color.FromArgb(206, 210, 216))
            b.FlatAppearance.BorderSize = If(on_, 2, 1)
            b.FlatAppearance.CheckedBackColor = Theme.Current.Primary
        Next
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
        Dim problem = Auth.EnterCompany(r, _company)
        If problem <> "" Then
            lblError.Text = problem
            Return
        End If
        LoggedInUserID = r.UserID
        LoggedInFullName = r.FullName
        LoggedInRole = r.Role
        Me.DialogResult = DialogResult.OK
        Me.Close()
    End Sub

End Class
