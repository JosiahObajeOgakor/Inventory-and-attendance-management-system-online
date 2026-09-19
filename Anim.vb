Imports System.Configuration
Imports System.Diagnostics
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Windows.Forms

''' Small, cheap UI animations built on WinForms timers (~60 fps):
'''   • Tween            — run a step callback from 0→1 with easing
'''   • CountUp          — a metric value counts up from zero to its figure
'''   • RevealCard       — metric card fades its text in and grows its stripe
'''   • FadeInOver       — screen transition: a fading veil over a region
'''   • PopIn            — dialogs fade and rise into place
'''   • SuccessTick      — a tick that draws itself, then fades away
'''   • Busy             — spinner on its own thread while the UI thread works
''' All of it is switched off with App.config  Animations = false.
Public Module Anim

    Private _enabled As Boolean?
    Public ReadOnly Property Enabled As Boolean
        Get
            If Not _enabled.HasValue Then
                Dim v = ConfigurationManager.AppSettings("Animations")
                _enabled = Not String.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
            End If
            Return _enabled.Value
        End Get
    End Property

    ' ===== easing =====
    Public Function EaseOutCubic(t As Double) As Double
        Return 1 - Math.Pow(1 - t, 3)
    End Function

    ''' Overshoots a touch and settles — the "bounce into place" feel.
    Public Function EaseOutBack(t As Double) As Double
        Const c1 = 1.70158, c3 = c1 + 1
        Return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2)
    End Function

    Public Function Lerp(a As Double, b As Double, t As Double) As Double
        Return a + (b - a) * t
    End Function

    Public Function LerpColor(a As Color, b As Color, t As Double) As Color
        t = Math.Max(0, Math.Min(1, t))
        Return Color.FromArgb(CInt(Lerp(a.R, b.R, t)), CInt(Lerp(a.G, b.G, t)), CInt(Lerp(a.B, b.B, t)))
    End Function

    ''' Runs `stepFn(easedProgress)` every frame for `durationMs` (after an
    ''' optional delay), always finishing with exactly 1.0, then `done`.
    ''' Animations off → jumps straight to the end state.
    Public Function Tween(durationMs As Integer, stepFn As Action(Of Double),
                          Optional done As Action = Nothing,
                          Optional ease As Func(Of Double, Double) = Nothing,
                          Optional delayMs As Integer = 0) As System.Windows.Forms.Timer
        If Not Enabled OrElse durationMs <= 0 Then
            SafeRun(Sub() stepFn(1.0))
            If done IsNot Nothing Then SafeRun(done)
            Return Nothing
        End If
        Dim easing = If(ease, AddressOf EaseOutCubic)
        Dim sw = Stopwatch.StartNew()
        Dim tm As New System.Windows.Forms.Timer() With {.Interval = 15}
        AddHandler tm.Tick, Sub(s, e)
                                Dim elapsed = sw.ElapsedMilliseconds - delayMs
                                If elapsed < 0 Then Return
                                Dim t = Math.Min(1.0, elapsed / CDbl(durationMs))
                                Dim ok = SafeRun(Sub() stepFn(If(t >= 1.0, 1.0, easing(t))))
                                If t >= 1.0 OrElse Not ok Then
                                    tm.Stop()
                                    tm.Dispose()
                                    If ok AndAlso done IsNot Nothing Then SafeRun(done)
                                End If
                            End Sub
        tm.Start()
        Return tm
    End Function

    ''' A step touching a control that's since been closed must end the
    ''' animation quietly, never crash the screen.
    Private Function SafeRun(a As Action) As Boolean
        Try
            a()
            Return True
        Catch
            Return False
        End Try
    End Function

    ' ===== metric cards =====

    ''' Counts the first number inside `lbl.Text` up from zero, keeping any
    ''' currency sign / suffix and the original number format ("₦1,250,000.00",
    ''' "42", "3.5%"). The label is held at its final size so the layout
    ''' doesn't shuffle while digits grow.
    Public Sub CountUp(lbl As Label, Optional durationMs As Integer = 900, Optional delayMs As Integer = 0)
        Dim finalText = lbl.Text
        Dim m = System.Text.RegularExpressions.Regex.Match(finalText, "-?\d[\d,]*(\.\d+)?")
        If Not m.Success Then Return
        ' One figure only — dates and "3 of 5" style text stay as they are.
        If finalText.Remove(m.Index, m.Length).Any(AddressOf Char.IsDigit) Then Return
        Dim target As Decimal
        If Not Decimal.TryParse(m.Value.Replace(",", ""), Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture, target) Then Return
        If target = 0D Then Return
        Dim decimals = If(m.Groups(1).Success, m.Groups(1).Value.Length - 1, 0)
        Dim fmt = If(m.Value.Contains(","), "#,0", "0") & If(decimals > 0, "." & New String("0"c, decimals), "")
        Dim prefix = finalText.Substring(0, m.Index), suffix = finalText.Substring(m.Index + m.Length)

        lbl.MinimumSize = lbl.PreferredSize
        lbl.Text = prefix & 0D.ToString(fmt, Globalization.CultureInfo.InvariantCulture) & suffix
        Tween(durationMs,
              Sub(t)
                  If lbl.IsDisposed Then Throw New ObjectDisposedException("lbl")
                  lbl.Text = If(t >= 1.0, finalText,
                                prefix & (target * CDec(t)).ToString(fmt, Globalization.CultureInfo.InvariantCulture) & suffix)
              End Sub, delayMs:=delayMs)
    End Sub

    ''' A metric card's entrance: its accent stripe sweeps across, the text
    ''' fades up out of the card colour, and the value counts up. Cards in the
    ''' same row come in one after another.
    Public Sub RevealCard(frame As Control, stripe As Control, title As Label, value As Label)
        If Not Enabled Then Return
        Dim titleColor = title.ForeColor, valueColor = value.ForeColor
        Dim bg = If(value.Parent IsNot Nothing, value.Parent.BackColor, Theme.Current.Surface)
        title.ForeColor = bg
        value.ForeColor = bg
        stripe.Dock = DockStyle.None
        stripe.Width = 0

        AddHandler frame.HandleCreated,
            Sub(s, e)
                Dim idx = If(frame.Parent IsNot Nothing, frame.Parent.Controls.GetChildIndex(frame), 0)
                Dim delay = Math.Min(idx, 8) * 90
                Tween(520, Sub(t)
                               If frame.IsDisposed Then Throw New ObjectDisposedException("card")
                               Dim host = stripe.Parent
                               stripe.Height = host.Height
                               stripe.Width = CInt(host.Width * t)
                               title.ForeColor = LerpColor(bg, titleColor, t)
                               value.ForeColor = LerpColor(bg, valueColor, t)
                           End Sub,
                      done:=Sub() stripe.Dock = DockStyle.Fill,
                      delayMs:=delay)
                CountUp(value, 950, delay + 80)
            End Sub
    End Sub

    ' ===== screen transitions =====

    ''' Fades a new screen in: a borderless veil in the window colour sits over
    ''' `region` and dissolves, while `slide` (the new screen) rises a few
    ''' pixels into place. Click-through and never takes focus.
    Public Sub FadeInOver(region As Control, Optional slide As Control = Nothing)
        If Not Enabled OrElse region Is Nothing OrElse Not region.IsHandleCreated OrElse Not region.Visible Then Return
        Dim owner = region.FindForm()
        If owner Is Nothing OrElse Not owner.Visible OrElse owner.WindowState = FormWindowState.Minimized Then Return

        Dim veil As New VeilForm() With {
            .BackColor = Theme.Current.WindowBg,
            .Bounds = region.RectangleToScreen(region.ClientRectangle),
            .Opacity = 0.96}
        veil.Show(owner)
        Const rise = 18
        If slide IsNot Nothing Then slide.Top = rise
        Tween(260, Sub(t)
                       If Not veil.IsDisposed Then veil.Opacity = 0.96 * (1 - t)
                       If slide IsNot Nothing AndAlso Not slide.IsDisposed Then slide.Top = CInt(rise * (1 - t))
                   End Sub,
              done:=Sub() veil.Close())
    End Sub

    Private NotInheritable Class VeilForm
        Inherits Form
        Public Sub New()
            FormBorderStyle = FormBorderStyle.None
            ShowInTaskbar = False
            StartPosition = FormStartPosition.Manual
        End Sub
        Protected Overrides ReadOnly Property ShowWithoutActivation As Boolean
            Get
                Return True
            End Get
        End Property
        Protected Overrides ReadOnly Property CreateParams As CreateParams
            Get
                Const WS_EX_NOACTIVATE = &H8000000, WS_EX_TOOLWINDOW = &H80, WS_EX_TRANSPARENT = &H20
                Dim cp = MyBase.CreateParams
                cp.ExStyle = cp.ExStyle Or WS_EX_NOACTIVATE Or WS_EX_TOOLWINDOW Or WS_EX_TRANSPARENT
                Return cp
            End Get
        End Property
    End Class

    ' ===== dialogs =====

    ''' Dialog entrance: fades in and rises ~14px into its final spot.
    ''' Call from the constructor; it hooks Load/Shown itself.
    Public Sub PopIn(f As Form)
        If Not Enabled Then Return
        Dim finalTop = 0
        AddHandler f.Load, Sub(s, e) f.Opacity = 0
        AddHandler f.Shown, Sub(s, e)
                                finalTop = f.Top
                                Const rise = 14
                                f.Top = finalTop + rise
                                Tween(230, Sub(t)
                                               If f.IsDisposed Then Throw New ObjectDisposedException("f")
                                               f.Opacity = t
                                               f.Top = finalTop + CInt(rise * (1 - t))
                                           End Sub)
                            End Sub
    End Sub

    ' ===== success tick =====

    ''' A tick that draws itself inside a circle over `owner`, with a short
    ''' caption, then fades away. Non-blocking and never steals focus.
    Public Sub SuccessTick(Optional owner As Form = Nothing, Optional caption As String = "Saved")
        If Not Enabled Then Return
        Dim host = If(owner, Form.ActiveForm)
        If host IsNot Nothing AndAlso (host.IsDisposed OrElse Not host.Visible) Then host = Nothing
        Dim area = If(host IsNot Nothing, host.Bounds, Screen.PrimaryScreen.WorkingArea)
        Dim f As New TickForm(caption)
        f.Location = New Point(area.Left + (area.Width - f.Width) \ 2, area.Top + (area.Height - f.Height) \ 2)
        f.Opacity = 0
        f.Show()
        ' circle 0–45%, tick 40–80%, hold, fade out.
        Tween(1500, Sub(t)
                        If f.IsDisposed Then Throw New ObjectDisposedException("tick")
                        f.Progress = t
                        f.Opacity = If(t < 0.12, t / 0.12, If(t > 0.82, Math.Max(0, (1 - t) / 0.18), 1.0))
                        f.Invalidate()
                    End Sub,
              done:=Sub() f.Close(),
              ease:=Function(t) t)
    End Sub

    Private NotInheritable Class TickForm
        Inherits Form
        Public Progress As Double
        Private ReadOnly _caption As String
        Private ReadOnly _font As New Font("Segoe UI", 11, FontStyle.Bold)
        Private ReadOnly _green As Color = Theme.FromHex("#2E7D32")

        Public Sub New(caption As String)
            _caption = caption
            FormBorderStyle = FormBorderStyle.None
            ShowInTaskbar = False
            TopMost = True
            StartPosition = FormStartPosition.Manual
            DoubleBuffered = True
            BackColor = Theme.Current.Surface
            Size = New Size(210, 180)
            Using gp = RoundedRect(New Rectangle(0, 0, Width, Height), 18)
                Region = New Region(gp)
            End Using
        End Sub

        Protected Overrides ReadOnly Property ShowWithoutActivation As Boolean
            Get
                Return True
            End Get
        End Property
        Protected Overrides ReadOnly Property CreateParams As CreateParams
            Get
                Const WS_EX_NOACTIVATE = &H8000000, WS_EX_TOOLWINDOW = &H80
                Dim cp = MyBase.CreateParams
                cp.ExStyle = cp.ExStyle Or WS_EX_NOACTIVATE Or WS_EX_TOOLWINDOW
                Return cp
            End Get
        End Property

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            Dim d = 84, cx = Width \ 2, cy = 70
            Dim circle As New Rectangle(cx - d \ 2, cy - d \ 2, d, d)

            Dim pc = Math.Min(1.0, Progress / 0.45)
            If pc > 0 Then
                Using fill As New SolidBrush(Color.FromArgb(CInt(40 * pc), _green))
                    g.FillEllipse(fill, circle)
                End Using
                Using pen As New Pen(_green, 5) With {.StartCap = LineCap.Round, .EndCap = LineCap.Round}
                    g.DrawArc(pen, circle, -90, CSng(360 * pc))
                End Using
            End If

            ' Tick: two strokes, the short one then the long one.
            Dim pk = Math.Max(0, Math.Min(1.0, (Progress - 0.4) / 0.4))
            If pk > 0 Then
                Dim a As New PointF(cx - 20, cy + 1), b As New PointF(cx - 6, cy + 16), c As New PointF(cx + 22, cy - 14)
                Dim len1 = 0.35
                Using pen As New Pen(_green, 7) With {.StartCap = LineCap.Round, .EndCap = LineCap.Round, .LineJoin = LineJoin.Round}
                    If pk <= len1 Then
                        Dim u = pk / len1
                        g.DrawLine(pen, a, New PointF(CSng(Lerp(a.X, b.X, u)), CSng(Lerp(a.Y, b.Y, u))))
                    Else
                        Dim u = (pk - len1) / (1 - len1)
                        g.DrawLines(pen, {a, b, New PointF(CSng(Lerp(b.X, c.X, u)), CSng(Lerp(b.Y, c.Y, u)))})
                    End If
                End Using
            End If

            Dim ta = Math.Max(0, Math.Min(1.0, (Progress - 0.5) / 0.25))
            Using b As New SolidBrush(LerpColor(BackColor, Theme.Current.TextPrimary, ta))
                Dim r As New RectangleF(8, cy + d \ 2 + 12, Width - 16, 40)
                Using sf As New StringFormat() With {.Alignment = StringAlignment.Center, .Trimming = StringTrimming.EllipsisCharacter}
                    g.DrawString(_caption, _font, b, r, sf)
                End Using
            End Using
        End Sub
    End Class

    Friend Function RoundedRect(r As Rectangle, radius As Integer) As GraphicsPath
        Dim gp As New GraphicsPath()
        Dim d = radius * 2
        gp.AddArc(r.X, r.Y, d, d, 180, 90)
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90)
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90)
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90)
        gp.CloseFigure()
        Return gp
    End Function

    ' ===== busy spinner =====

    ''' Shows a spinner while slow work runs on the UI thread:
    '''     Using Anim.Busy("Building report…")
    '''         ...slow work...
    '''     End Using
    ''' The spinner lives on its own thread (so it keeps turning while the UI
    ''' thread is busy) and only appears if the work takes over ~350ms.
    Public Function Busy(Optional message As String = "Working…") As IDisposable
        Return New BusyScope(message)
    End Function

    Private NotInheritable Class BusyScope
        Implements IDisposable
        Private _form As SpinnerForm
        Private ReadOnly _ready As New ManualResetEventSlim(False)
        Private _disposed As Integer

        Public Sub New(message As String)
            If Not Enabled Then Return
            Dim host = Form.ActiveForm
            Dim area = If(host IsNot Nothing AndAlso host.Visible, host.Bounds, Screen.PrimaryScreen.WorkingArea)
            Dim th As New Thread(Sub()
                                     Try
                                         _form = New SpinnerForm(message)
                                         _form.Location = New Point(area.Left + (area.Width - _form.Width) \ 2,
                                                                    area.Top + (area.Height - _form.Height) \ 2)
                                         AddHandler _form.HandleCreated, Sub(s, e) _ready.Set()
                                         Application.Run(_form)
                                     Catch
                                     Finally
                                         _ready.Set()
                                     End Try
                                 End Sub) With {.IsBackground = True}
            th.SetApartmentState(ApartmentState.STA)
            th.Start()
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Interlocked.Exchange(_disposed, 1) = 1 OrElse Not Enabled Then Return
            _ready.Wait(2000)
            Try
                Dim f = _form
                If f IsNot Nothing AndAlso f.IsHandleCreated AndAlso Not f.IsDisposed Then f.BeginInvoke(New Action(AddressOf f.Close))
            Catch
            End Try
        End Sub
    End Class

    Private NotInheritable Class SpinnerForm
        Inherits Form
        Private ReadOnly _message As String
        Private ReadOnly _spin As New System.Windows.Forms.Timer() With {.Interval = 16}
        Private ReadOnly _clock As Stopwatch = Stopwatch.StartNew()
        Private ReadOnly _font As New Font("Segoe UI", 10)
        Private ReadOnly _accent As Color = Theme.Current.Primary
        Private ReadOnly _text As Color = Theme.Current.TextPrimary

        Public Sub New(message As String)
            _message = message
            FormBorderStyle = FormBorderStyle.None
            ShowInTaskbar = False
            TopMost = True
            StartPosition = FormStartPosition.Manual
            DoubleBuffered = True
            BackColor = Theme.Current.Surface
            Size = New Size(240, 130)
            Opacity = 0
            Using gp = RoundedRect(New Rectangle(0, 0, Width, Height), 16)
                Region = New Region(gp)
            End Using
            AddHandler _spin.Tick, Sub(s, e)
                                       Dim ms = _clock.ElapsedMilliseconds
                                       ' Stay invisible for quick jobs; fade in after 350ms.
                                       Opacity = Math.Max(0, Math.Min(1.0, (ms - 350) / 200.0))
                                       Invalidate()
                                   End Sub
            AddHandler Shown, Sub(s, e) _spin.Start()
            AddHandler FormClosed, Sub(s, e) _spin.Dispose()
        End Sub

        Protected Overrides ReadOnly Property ShowWithoutActivation As Boolean
            Get
                Return True
            End Get
        End Property
        Protected Overrides ReadOnly Property CreateParams As CreateParams
            Get
                Const WS_EX_NOACTIVATE = &H8000000, WS_EX_TOOLWINDOW = &H80
                Dim cp = MyBase.CreateParams
                cp.ExStyle = cp.ExStyle Or WS_EX_NOACTIVATE Or WS_EX_TOOLWINDOW
                Return cp
            End Get
        End Property

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            Dim d = 44, cx = Width \ 2, cy = 46
            Dim r As New Rectangle(cx - d \ 2, cy - d \ 2, d, d)
            Using track As New Pen(Color.FromArgb(40, _accent), 5)
                g.DrawEllipse(track, r)
            End Using
            Dim ms = _clock.ElapsedMilliseconds
            Dim start = CSng((ms * 0.36) Mod 360)
            ' The arc breathes between short and long as it turns.
            Dim sweep = CSng(70 + 200 * (0.5 + 0.5 * Math.Sin(ms / 420.0)))
            Using pen As New Pen(_accent, 5) With {.StartCap = LineCap.Round, .EndCap = LineCap.Round}
                g.DrawArc(pen, r, start, sweep)
            End Using
            Using b As New SolidBrush(_text), sf As New StringFormat() With {.Alignment = StringAlignment.Center}
                g.DrawString(_message, _font, b, New RectangleF(8, cy + d \ 2 + 12, Width - 16, 30), sf)
            End Using
        End Sub
    End Class

End Module
