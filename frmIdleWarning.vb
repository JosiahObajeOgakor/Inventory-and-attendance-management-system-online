Imports System.Drawing
Imports System.Windows.Forms

''' Admin inactivity warning: a modal counting down (3 minutes by default).
''' "Stay signed in" returns OK; the countdown reaching zero or "Sign out now"
''' returns Abort, and the caller signs the admin out.
Public Class frmIdleWarning
    Inherits Form

    Private ReadOnly _endsAt As DateTime
    Private ReadOnly _tick As New Timer() With {.Interval = 40}
    Private ReadOnly _red As Color = Color.FromArgb(200, 40, 40)
    Private ReadOnly _redSoft As Color = Color.FromArgb(245, 170, 170)
    Private ReadOnly lblCountdown As New Label() With {
        .Dock = DockStyle.Top, .Height = 64, .TextAlign = ContentAlignment.MiddleCenter, .Tag = "keepfont"}

    Public Sub New(countdown As TimeSpan)
        _endsAt = DateTime.UtcNow + countdown
        Text = "Are you still there?"
        ClientSize = New Size(440, 230)
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterScreen
        MinimizeBox = False : MaximizeBox = False : ControlBox = False
        ShowInTaskbar = True
        TopMost = True

        Dim msg As New Label() With {
            .Dock = DockStyle.Top, .Height = 70, .Padding = New Padding(22, 20, 22, 0), .UseMnemonic = False,
            .Text = "You've been inactive for a while. For security, this admin session will be signed out automatically in:"}
        lblCountdown.Font = New Font(Theme.BaseFont().FontFamily, 26, FontStyle.Bold)

        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .FlowDirection = FlowDirection.RightToLeft, .AutoSize = True, .Padding = New Padding(16)}
        Dim btnStay As New Button() With {.Text = "Stay signed in", .AutoSize = True, .Tag = "primary", .DialogResult = DialogResult.OK}
        Dim btnOut As New Button() With {.Text = "Sign out now", .AutoSize = True, .Tag = "danger", .DialogResult = DialogResult.Abort}
        bar.Controls.Add(btnStay)
        bar.Controls.Add(btnOut)
        AcceptButton = btnStay

        Controls.Add(lblCountdown)
        Controls.Add(msg)
        Controls.Add(bar)

        AddHandler _tick.Tick, AddressOf UpdateCountdown
        AddHandler Shown, Sub(s, e)
                              UpdateCountdown(Nothing, EventArgs.Empty)
                              _tick.Start()
                              Activate()
                          End Sub
        AddHandler FormClosed, Sub(s, e) _tick.Dispose()
        Theme.Apply(Me)
        Theme.StylePrimaryButton(btnStay)
        Theme.StyleDangerButton(btnOut)
        lblCountdown.ForeColor = _red
        Anim.PopIn(Me)
    End Sub

    Private Sub UpdateCountdown(sender As Object, e As EventArgs)
        Dim left = _endsAt - DateTime.UtcNow
        If left <= TimeSpan.Zero Then
            _tick.Stop()
            DialogResult = DialogResult.Abort
            Close()
            Return
        End If
        Dim secs = CInt(Math.Ceiling(left.TotalSeconds))
        Dim txt = $"{secs \ 60}:{secs Mod 60:00}"
        If lblCountdown.Text <> txt Then lblCountdown.Text = txt
        ' Last 30 seconds: the numbers pulse, once a second.
        If Anim.Enabled AndAlso left.TotalSeconds <= 30 Then
            Dim phase = left.TotalSeconds - Math.Floor(left.TotalSeconds)
            lblCountdown.ForeColor = Anim.LerpColor(_redSoft, _red, phase)
        End If
    End Sub

End Class
