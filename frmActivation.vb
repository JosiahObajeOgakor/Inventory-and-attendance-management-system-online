Imports System.Windows.Forms
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.IO

''' Activation. Two panels: a branded hero on the left (landing photo, slowly
''' drifting behind a soft gradient) and the actual controls on the right —
''' email, one "Pay now" button that opens the AlatPay plugin (card, transfer,
''' USSD), and "I've paid". Vendors can instead enter a one-time installation
''' code or an offline key. "Skip for now" opens a locked app.
Public Class frmActivation
    Inherits Form

    Private ReadOnly hero As New HeroPanel()
    Private ReadOnly txtEmail As New TextBox() With {.Width = 340, .BorderStyle = BorderStyle.FixedSingle}
    Private ReadOnly lblStatus As New Label() With {.AutoSize = False, .Width = 470, .Height = 54, .Tag = "keepfont", .Padding = New Padding(0, 6, 0, 0)}
    Private ReadOnly btnPay As New Button() With {.Text = "Pay now  —  card · bank transfer · USSD", .Tag = "primary", .Height = 46, .Width = 380}
    Private ReadOnly btnActivate As New Button() With {.Text = "I've already paid — activate now", .Height = 42, .Width = 380}
    Private ReadOnly txtCode As New TextBox() With {.Width = 270, .Font = New Font("Consolas", 10), .BorderStyle = BorderStyle.FixedSingle}
    Private ReadOnly codeRow As New FlowLayoutPanel() With {.AutoSize = True, .Dock = DockStyle.Top, .Visible = False, .Margin = New Padding(0)}
    Private ReadOnly lnkCode As New LinkLabel() With {.Text = "Have an installation code or offline key?", .AutoSize = True, .Margin = New Padding(0, 12, 0, 6), .Tag = "keepfont"}

    ' Fade the window in, and animate the "working…" dots during network calls.
    Private ReadOnly fade As New Timer() With {.Interval = 25}
    Private ReadOnly busy As New Timer() With {.Interval = 300}
    Private busyText As String
    Private busyDots As Integer

    Public ReadOnly Property IsActivatedNow As Boolean
    Public ReadOnly Property Skipped As Boolean

    Public Sub New()
        Text = Theme.AppName & " — Activate this installation"
        ClientSize = New Size(960, 600)
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterScreen
        MinimizeBox = False : MaximizeBox = False
        BackColor = Theme.Current.Surface
        Opacity = 0

        hero.Dock = DockStyle.Left
        hero.Width = 380

        Controls.Add(BuildPane())
        Controls.Add(hero)

        AddHandler fade.Tick, Sub(s, e)
                                  Opacity = Math.Min(1, Opacity + 0.08)
                                  If Opacity >= 1 Then fade.Stop()
                              End Sub
        AddHandler busy.Tick, Sub(s, e)
                                  busyDots = (busyDots + 1) Mod 4
                                  lblStatus.Text = busyText & New String("."c, busyDots)
                              End Sub
        AddHandler Load, Sub(s, e)
                             fade.Start()
                             hero.Start()
                         End Sub
        AddHandler FormClosed, Sub(s, e)
                                   fade.Stop() : busy.Stop() : hero.Stop()
                               End Sub

        Theme.Apply(Me)
        ' Theme.Apply resets fonts — restore the ones this screen sets deliberately.
        txtCode.Font = New Font("Consolas", 10)
        btnPay.Font = New Font("Segoe UI", Theme.BaseFontSize, FontStyle.Bold)
        UiHelpers.FitToScreen(Me)
    End Sub

    ''' Everything to the right of the hero image.
    Private Function BuildPane() As Control
        Dim pane As New Panel() With {.Dock = DockStyle.Fill, .Padding = New Padding(38, 30, 38, 12), .BackColor = Theme.Current.Surface, .AutoScroll = True}

        Dim stack As New TableLayoutPanel() With {.Dock = DockStyle.Top, .ColumnCount = 1, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink}

        stack.Controls.Add(New Label() With {
            .Text = "Activate this computer", .AutoSize = True, .Tag = "keepfont",
            .Font = New Font("Segoe UI", 20, FontStyle.Bold), .ForeColor = Theme.Current.TextPrimary,
            .Margin = New Padding(0, 0, 0, 4)})
        stack.Controls.Add(New Label() With {
            .Text = "$250 once — then it stays activated on this computer for good, even after a reinstall and with no internet. Moving to a new computer takes one click.",
            .AutoSize = True, .MaximumSize = New Size(470, 0), .Tag = "keepfont",
            .ForeColor = Theme.Current.TextMuted, .Margin = New Padding(0, 0, 0, 22)})

        stack.Controls.Add(FieldLabel("YOUR EMAIL"))
        txtEmail.Text = Licensing.SavedEmail()
        Dim emailHost As New Panel() With {.Height = 38, .Width = 380, .Margin = New Padding(0, 0, 0, 18), .Padding = New Padding(0, 4, 0, 0)}
        txtEmail.Dock = DockStyle.Top
        emailHost.Controls.Add(txtEmail)
        stack.Controls.Add(emailHost)

        Theme.StylePrimaryButton(btnPay)
        btnPay.FlatAppearance.BorderSize = 0
        btnPay.Margin = New Padding(0, 0, 0, 10)
        StyleQuiet(btnActivate)
        btnActivate.Margin = New Padding(0, 0, 0, 6)
        AddHandler btnPay.Click, AddressOf Pay_Click
        AddHandler btnActivate.Click, AddressOf Activate_Click
        stack.Controls.Add(btnPay)
        stack.Controls.Add(btnActivate)

        lblStatus.ForeColor = Theme.Current.TextMuted
        lblStatus.Text = "Enter your email, tap Pay now, choose any payment method, then come back and tap ""I've already paid""."
        stack.Controls.Add(lblStatus)

        ' Vendor-only: one-time installation code, or an offline key for this Machine ID.
        Dim btnCode As New Button() With {.Text = "Activate", .AutoSize = True, .Margin = New Padding(6, 0, 0, 0)}
        StyleQuiet(btnCode)
        AddHandler btnCode.Click, AddressOf ActivateCode_Click
        codeRow.Controls.Add(txtCode)
        codeRow.Controls.Add(btnCode)
        AddHandler lnkCode.Click, Sub(s, e)
                                      codeRow.Visible = Not codeRow.Visible
                                      If codeRow.Visible Then txtCode.Focus()
                                  End Sub
        lnkCode.LinkColor = Theme.Current.Primary
        stack.Controls.Add(lnkCode)
        stack.Controls.Add(codeRow)

        stack.Controls.Add(New Panel() With {.Height = 14, .Width = 10})   ' breathing space
        stack.Controls.Add(FieldLabel("MACHINE ID  —  THIS COMPUTER"))
        Dim idRow As New FlowLayoutPanel() With {.AutoSize = True, .Margin = New Padding(0, 2, 0, 0)}
        idRow.Controls.Add(New Label() With {
            .Text = Licensing.MachineId, .AutoSize = True, .Tag = "keepfont",
            .Font = New Font("Consolas", 13, FontStyle.Bold), .ForeColor = Theme.Current.TextPrimary,
            .Margin = New Padding(0, 2, 12, 0)})
        Dim btnCopy As New Button() With {.Text = "Copy", .AutoSize = True}
        StyleQuiet(btnCopy)
        AddHandler btnCopy.Click, Sub(s, e)
                                      Clipboard.SetText(Licensing.MachineId)
                                      AppUI.Toast("Machine ID copied.", AppUI.ToastKind.Info, Me)
                                  End Sub
        idRow.Controls.Add(btnCopy)
        stack.Controls.Add(idRow)

        Dim bottom As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(0, 10, 0, 0)}
        Dim btnQuit As New Button() With {.Text = "Quit", .AutoSize = True}
        StyleQuiet(btnQuit)
        AddHandler btnQuit.Click, Sub(s, e) Close()
        Dim lnkSkip As New LinkLabel() With {.Text = "Skip for now", .AutoSize = True, .Margin = New Padding(0, 8, 16, 0), .Tag = "keepfont"}
        lnkSkip.LinkColor = Theme.Current.TextMuted
        AddHandler lnkSkip.Click, Sub(s, e)
                                      _Skipped = True
                                      DialogResult = DialogResult.OK
                                      Close()
                                  End Sub
        bottom.Controls.Add(btnQuit)
        bottom.Controls.Add(lnkSkip)

        pane.Controls.Add(stack)
        pane.Controls.Add(bottom)
        Return pane
    End Function

    Private Function FieldLabel(text As String) As Label
        Return New Label() With {
            .Text = text, .AutoSize = True, .Tag = "keepfont",
            .Font = New Font("Segoe UI", 8.5F, FontStyle.Bold), .ForeColor = Theme.Current.TextMuted,
            .Margin = New Padding(0, 0, 0, 2)}
    End Function

    ''' Outline button that lifts on hover — used for everything but the main action.
    Private Sub StyleQuiet(b As Button)
        b.FlatStyle = FlatStyle.Flat
        b.BackColor = Theme.Current.Surface
        b.ForeColor = Theme.Current.TextPrimary
        b.FlatAppearance.BorderColor = Theme.Current.GridLineColor
        b.FlatAppearance.BorderSize = 1
        b.FlatAppearance.MouseOverBackColor = Theme.Current.GridSelectionBg
        b.Padding = New Padding(10, 6, 10, 6)
        b.Cursor = Cursors.Hand
    End Sub

    Private Sub SetBusy(message As String)
        busyText = message
        busyDots = 0
        lblStatus.ForeColor = Theme.Current.TextPrimary
        lblStatus.Text = message
        busy.Start()
        Cursor = Cursors.WaitCursor
        Application.DoEvents()
    End Sub

    Private Sub EndBusy(message As String, Optional bad As Boolean = False)
        busy.Stop()
        Cursor = Cursors.Default
        lblStatus.ForeColor = If(bad, Color.Firebrick, Theme.Current.TextMuted)
        lblStatus.Text = message
    End Sub

    Private Function ValidEmail() As Boolean
        Dim e = txtEmail.Text.Trim()
        If e.Length < 5 OrElse Not e.Contains("@") OrElse Not e.Contains(".") Then
            EndBusy("Enter a valid email address first.", bad:=True)
            txtEmail.Focus()
            Return False
        End If
        Return True
    End Function

    Private Sub Pay_Click(sender As Object, e As EventArgs)
        If Not ValidEmail() Then Return
        btnPay.Enabled = False
        SetBusy("Opening the secure payment page")

        Dim r = Licensing.StartPurchase(txtEmail.Text, "NG")
        btnPay.Enabled = True

        If r.AlreadyPaid Then
            EndBusy("This email has already paid — just tap ""I've already paid"".")
            Return
        End If
        If Not r.Ok OrElse String.IsNullOrEmpty(r.Reference) Then
            EndBusy(If(String.IsNullOrEmpty(r.ErrorMessage), "Could not start the payment. Try again shortly.", r.ErrorMessage), bad:=True)
            Return
        End If

        Dim payUrl = If(String.IsNullOrEmpty(r.PayUrl), Licensing.ApiBaseUrl & "/pay?ref=" & Uri.EscapeDataString(r.Reference), r.PayUrl)
        Try
            Process.Start(payUrl)
            EndBusy("Pay in your browser — card, bank transfer or USSD. When it's done, come back and tap ""I've already paid"".")
        Catch
            EndBusy("Couldn't open your browser. Visit: " & payUrl, bad:=True)
        End Try
    End Sub

    Private Sub ActivateCode_Click(sender As Object, e As EventArgs)
        Dim entered = System.Text.RegularExpressions.Regex.Replace(txtCode.Text, "\s", "")
        If entered = "" Then
            EndBusy("Enter the installation code or offline key first.", bad:=True)
            Return
        End If

        ' Offline keys are ~86 chars; one-time codes are 12 (14 with dashes).
        If entered.Length > 40 Then
            If Licensing.ActivateWithOfflineKey(entered) Then
                ActivationSucceeded("Activated. Thank you!")
            Else
                EndBusy("That offline key isn't valid for this computer. It must be generated for Machine ID " & Licensing.MachineId & ".", bad:=True)
            End If
            Return
        End If

        If Not ValidEmail() Then Return
        SetBusy("Checking your code")
        Dim r = Licensing.ActivateWithCode(txtEmail.Text, entered)
        busy.Stop()
        Cursor = Cursors.Default

        Select Case r.Status
            Case Licensing.ActivationStatus.Activated
                ActivationSucceeded("Activated. Thank you!")
            Case Licensing.ActivationStatus.TransferRequired
                EndBusy(If(r.Message, "This email already has a licence on another computer."), bad:=True)
            Case Else
                EndBusy(If(r.Message, "That code didn't work."), bad:=True)
        End Select
    End Sub

    Private Sub Activate_Click(sender As Object, e As EventArgs)
        If Not ValidEmail() Then Return
        btnActivate.Enabled = False
        SetBusy("Checking your payment")

        Dim r = Licensing.ActivateOnline(txtEmail.Text)
        btnActivate.Enabled = True
        busy.Stop()
        Cursor = Cursors.Default

        Select Case r.Status
            Case Licensing.ActivationStatus.Activated
                ActivationSucceeded("Activated. Thank you!")

            Case Licensing.ActivationStatus.Pending
                EndBusy("No completed payment yet for " & txtEmail.Text.Trim() & ". If you just paid, wait a moment and try again.", bad:=True)

            Case Licensing.ActivationStatus.TransferRequired
                If AppUI.Confirm(Me,
                    "This licence is currently active on another computer." & vbCrLf & vbCrLf &
                    "Move it to THIS computer? The other computer will stop working.",
                    "Transfer licence", "Move it here") Then
                    HandleTransferResult(Licensing.RequestTransfer(txtEmail.Text))
                End If

            Case Licensing.ActivationStatus.NoInternet
                EndBusy(r.Message, bad:=True)

            Case Else
                EndBusy(If(r.Message, "Activation failed. Try again shortly."), bad:=True)
        End Select
    End Sub

    Private Sub ActivationSucceeded(message As String)
        busy.Stop()
        Cursor = Cursors.Default
        _IsActivatedNow = True
        AppUI.Toast(message, AppUI.ToastKind.Success)
        DialogResult = DialogResult.OK
        Close()
    End Sub

    Private Sub HandleTransferResult(tr As Licensing.ActivationResult)
        Select Case tr.Status
            Case Licensing.ActivationStatus.Activated
                ActivationSucceeded("Licence moved to this computer.")
            Case Licensing.ActivationStatus.TransferPending
                EndBusy("Transfer request sent. You'll be able to activate here once it's approved.")
            Case Else
                EndBusy(If(tr.Message, "Could not request the transfer."), bad:=True)
        End Select
    End Sub

    ''' Branded left panel: the landing photo drifting slowly under a brand-colour
    ''' wash, with the logo and a few selling points drawn on top.
    Private NotInheritable Class HeroPanel
        Inherits Panel

        Private ReadOnly _photo As Image
        Private ReadOnly _timer As New Timer() With {.Interval = 50}
        Private _t As Double

        Public Sub New()
            DoubleBuffered = True
            BackColor = Color.FromArgb(58, 26, 92)
            _photo = LoadPhoto()
            AddHandler _timer.Tick, Sub(s, e)
                                        _t += 0.012
                                        Invalidate()
                                    End Sub
        End Sub

        Private Shared Function LoadPhoto() As Image
            Try
                Dim p = AppPaths.Asset("landing.jpg")
                If File.Exists(p) Then Return Image.FromFile(p)
            Catch
            End Try
            Return Nothing
        End Function

        Public Sub Start()
            _timer.Start()
        End Sub
        Public Shadows Sub [Stop]()
            _timer.Stop()
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            g.InterpolationMode = InterpolationMode.HighQualityBicubic
            g.SmoothingMode = SmoothingMode.AntiAlias
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.ClearTypeGridFit

            ' Slow drift (a gentle zoom-and-pan) so the panel feels alive.
            If _photo IsNot Nothing Then
                Dim zoom = 1.06F + 0.04F * CSng(Math.Sin(_t))
                Dim scale = Math.Max(Width / CSng(_photo.Width), Height / CSng(_photo.Height)) * zoom
                Dim w = _photo.Width * scale, h = _photo.Height * scale
                Dim dx = CSng((Width - w) / 2 + 10 * Math.Sin(_t * 0.7))
                Dim dy = CSng((Height - h) / 2 + 12 * Math.Cos(_t * 0.5))
                g.DrawImage(_photo, dx, dy, w, h)
            End If

            ' Brand wash: deep purple at the bottom, clearer at the top.
            Using b As New LinearGradientBrush(New Rectangle(0, 0, Width, Height),
                                               Color.FromArgb(150, 46, 18, 74), Color.FromArgb(242, 58, 26, 92), 90.0F)
                g.FillRectangle(b, 0, 0, Width, Height)
            End Using

            Dim x = 34
            If Theme.Logo IsNot Nothing Then
                Dim lh = 62.0F, lw = Theme.Logo.Width * (lh / Theme.Logo.Height)
                ' White plate so the logo reads on the dark wash.
                Using plate As New SolidBrush(Color.FromArgb(235, 255, 255, 255))
                    g.FillRectangle(plate, x - 8, 30, lw + 16, lh + 12)
                End Using
                g.DrawImage(Theme.Logo, CSng(x), 36.0F, lw, lh)
            End If

            Using title As New Font("Segoe UI", 24, FontStyle.Bold),
                  sub_ As New Font("Segoe UI", 10.5F),
                  item As New Font("Segoe UI", 10),
                  white As New SolidBrush(Color.White),
                  soft As New SolidBrush(Color.FromArgb(220, 233, 220, 250))
                g.DrawString(Theme.AppName, title, white, x - 4, 140)
                g.DrawString("Stock, sales and receipts for" & vbCrLf & AppInfo.CompanyName, sub_, soft, x - 2, 186)

                Dim y = 268
                For Each line In {"One payment — lifetime licence",
                                  "Works offline once activated",
                                  "Your data stays on this computer",
                                  "Receipts, waybills and reports as PDF"}
                    Using dot As New SolidBrush(Color.FromArgb(255, 240, 170, 60))
                        g.FillEllipse(dot, x - 2, y + 6, 7, 7)
                    End Using
                    g.DrawString(line, item, soft, x + 12, y)
                    y += 34
                Next
            End Using
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                _timer.Dispose()
                _photo?.Dispose()
            End If
            MyBase.Dispose(disposing)
        End Sub
    End Class

End Class
