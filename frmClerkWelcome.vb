Imports System.Windows.Forms
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms.Integration

''' Full-screen welcome shown when a warehouse clerk signs in (never for Admin):
''' the 4-second ChewyPets clip fills the screen with the clerk's name over it,
''' and a "Check In" card sits on top of it so she can clock in while it plays.
''' She must click Check In before the screen hands over to the dashboard — until
''' then, clicking elsewhere, pressing a key, the clip ending or the safety
''' timeout all do nothing; once she's checked in (just now, or already earlier
''' today) any of those move on. The card itself never triggers a skip.
''' If she doesn't want to check in, "Sign out instead" logs that and returns
''' to the login screen without ever reaching the dashboard.
'''
''' If the clip can't be played on this PC (missing file or no media codecs),
''' the landing photo is shown briefly instead — a welcome screen must never
''' block someone from working, and neither does attendance: a failure to check
''' in never stops the app opening.
Public Class frmClerkWelcome
    Inherits Form

    Private ReadOnly _userId As Integer
    Private ReadOnly _name As String
    Private _host As ElementHost
    Private _player As Windows.Controls.MediaElement
    Private ReadOnly _safety As New Timer() With {.Interval = 9000}   ' never hold the app for longer than this
    Private ReadOnly _hint As New Timer() With {.Interval = 1200}
    Private ReadOnly _clock As New Timer() With {.Interval = 1000}
    Private ReadOnly _advance As New Timer() With {.Interval = 900}   ' brief pause on the ✓ before handing over
    Private _showHint As Boolean
    Private _closing As Boolean

    ''' True when the clip actually started playing (used by the tests).
    Public ReadOnly Property VideoPlaying As Boolean

    ' ===== check-in card =====
    Private ReadOnly card As New CardPanel() With {.Size = New Size(280, 208), .CardColor = Color.White}
    Private ReadOnly lblDate As New Label() With {.AutoSize = True, .Tag = "keepfont", .ForeColor = Color.Gray, .Font = New Font("Segoe UI", 9)}
    Private ReadOnly lblClock As New Label() With {.AutoSize = True, .Tag = "keepfont", .ForeColor = Color.FromArgb(40, 40, 46), .Font = New Font("Consolas", 22, FontStyle.Bold)}
    Private ReadOnly lblCheckState As New Label() With {.AutoSize = True, .Tag = "keepfont", .Font = New Font("Segoe UI", 9.5F), .MaximumSize = New Size(230, 0)}
    Private ReadOnly btnCheckIn As New Button() With {.Text = "Check In", .Tag = "primary", .Width = 230, .Height = 36}
    Private ReadOnly btnSignOut As New LinkLabel() With {.Text = "Sign out instead", .AutoSize = True, .Tag = "keepfont", .Font = New Font("Segoe UI", 9F), .LinkColor = Color.FromArgb(60, 90, 150), .ActiveLinkColor = Color.FromArgb(30, 60, 120), .VisitedLinkColor = Color.FromArgb(60, 90, 150), .LinkBehavior = LinkBehavior.HoverUnderline, .Margin = New Padding(0, 10, 0, 0)}

    ''' True once she's checked in for today (used by the tests).
    Public ReadOnly Property CheckedIn As Boolean

    ''' True once she's chosen "Sign out instead" rather than checking in.
    Public ReadOnly Property SignedOut As Boolean

    Public Shared Function VideoPath() As String
        Return AppPaths.Asset("landingvideo.mp4")
    End Function

    ''' Only warehouse clerks get the welcome clip (and the check-in card).
    Public Shared Function AppliesTo(role As String) As Boolean
        Return Not String.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
    End Function

    ''' Shows the welcome full-screen for `role`, if that role gets one. Returns
    ''' False only when she chose "Sign out instead" — the caller should return
    ''' to the login screen rather than opening the app.
    Public Shared Function ShowFor(userId As Integer, role As String, fullName As String) As Boolean
        If Not AppliesTo(role) Then Return True
        Try
            Using f As New frmClerkWelcome(userId, fullName)
                f.ShowDialog()
                Return Not f.SignedOut
            End Using
        Catch
            Return True   ' a welcome screen is never worth blocking sign-in for
        End Try
    End Function

    Public Sub New(userId As Integer, fullName As String)
        _userId = userId
        _name = fullName
        FormBorderStyle = FormBorderStyle.None
        WindowState = FormWindowState.Maximized
        StartPosition = FormStartPosition.CenterScreen
        BackColor = Color.Black
        ShowInTaskbar = False
        KeyPreview = True
        DoubleBuffered = True

        BuildCard()

        AddHandler Load, Sub(s, e)
                             StartPlayback()
                             RefreshCheckInState()
                             RepositionCard()
                         End Sub
        AddHandler Resize, Sub(s, e) RepositionCard()
        AddHandler Click, Sub(s, e) TryFinish()
        AddHandler KeyDown, Sub(s, e) TryFinish()
        AddHandler _safety.Tick, Sub(s, e) TryFinish()
        AddHandler _advance.Tick, Sub(s, e)
                                      _advance.Stop()
                                      Finish()
                                  End Sub
        AddHandler _hint.Tick, Sub(s, e)
                                   _showHint = True
                                   _hint.Stop()
                                   Invalidate()
                               End Sub
        AddHandler _clock.Tick, Sub(s, e)
                                    lblClock.Text = DateTime.Now.ToString("HH:mm:ss")
                                End Sub
        AddHandler FormClosed, Sub(s, e)
                                   _safety.Stop() : _hint.Stop() : _clock.Stop() : _advance.Stop()
                                   Try
                                       _player?.Stop()
                                       _player?.Close()
                                   Catch
                                   End Try
                               End Sub
    End Sub

    ''' The clock-in card: date, live clock, and the Check In button — or the
    ''' already-checked-in state once she's clocked in. Sits over the video, not
    ''' inside it, so it always reads clearly regardless of what's playing.
    Private Sub BuildCard()
        card.Padding = New Padding(18, 14, 18, 14)
        Dim stack As New FlowLayoutPanel() With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.TopDown, .WrapContents = False}
        lblDate.Text = Date.Today.ToString("dddd, dd MMMM yyyy")
        lblClock.Text = DateTime.Now.ToString("HH:mm:ss")
        lblClock.Margin = New Padding(0, 2, 0, 8)
        lblCheckState.Margin = New Padding(0, 8, 0, 0)
        stack.Controls.Add(lblDate)
        stack.Controls.Add(lblClock)
        Theme.StylePrimaryButton(btnCheckIn)
        AddHandler btnCheckIn.Click, AddressOf CheckIn_Click
        stack.Controls.Add(btnCheckIn)
        stack.Controls.Add(lblCheckState)
        AddHandler btnSignOut.LinkClicked, AddressOf SignOut_Click
        stack.Controls.Add(btnSignOut)
        card.Controls.Add(stack)
        Controls.Add(card)
        card.BringToFront()   ' above the video, which is added later in StartPlayback
    End Sub

    Private Sub RepositionCard()
        card.Location = New Point(ClientSize.Width - card.Width - 32, 32)
    End Sub

    Private Sub RefreshCheckInState()
        Try
            Dim existing = Attendance.TodayCheckIn(_userId)
            If existing.HasValue Then
                ShowCheckedIn(existing.Value)
            Else
                btnCheckIn.Visible = True
                btnCheckIn.Enabled = True
                btnSignOut.Visible = True
                lblCheckState.Text = ""
            End If
        Catch
            ' If attendance can't be read, just let her check in normally on click.
        End Try
    End Sub

    Private Sub CheckIn_Click(sender As Object, e As EventArgs)
        btnCheckIn.Enabled = False
        Try
            Dim at = Attendance.CheckIn(_userId, _name)
            ShowCheckedIn(at)
            _advance.Start()   ' let her see the ✓ for a moment, then move on to the dashboard
        Catch ex As Exception
            btnCheckIn.Enabled = True
            lblCheckState.ForeColor = Color.Firebrick
            lblCheckState.Text = "Couldn't record check-in: " & ex.Message
        End Try
    End Sub

    ''' She doesn't want to check in right now — log it and hand straight back
    ''' to the login screen instead of the dashboard.
    Private Sub SignOut_Click(sender As Object, e As EventArgs)
        Try
            Attendance.DeclineAndSignOut(_userId, _name)
        Catch
        End Try
        _SignedOut = True
        Finish()
    End Sub

    Private Sub ShowCheckedIn(at As DateTime)
        _CheckedIn = True
        btnCheckIn.Visible = False
        btnSignOut.Visible = False
        lblCheckState.ForeColor = Color.FromArgb(22, 163, 74)
        lblCheckState.Font = New Font("Segoe UI", 9.5F, FontStyle.Bold)
        lblCheckState.Text = "✓ Checked In at " & at.ToString("HH:mm")
    End Sub

    Private Sub StartPlayback()
        _safety.Start()
        _hint.Start()
        _clock.Start()
        Dim clip = VideoPath()
        If Not File.Exists(clip) Then
            _safety.Interval = 9000   ' no clip — the card still gives her time to check in
            Return
        End If

        Try
            _player = New Windows.Controls.MediaElement() With {
                .LoadedBehavior = Windows.Controls.MediaState.Manual,
                .UnloadedBehavior = Windows.Controls.MediaState.Manual,
                .Stretch = Windows.Media.Stretch.UniformToFill,
                .Volume = 0}
            AddHandler _player.MediaOpened, Sub(s, e) _VideoPlaying = True
            ' Loops while she hasn't checked in yet; hands over once she has.
            AddHandler _player.MediaEnded, Sub(s, e)
                                               If CheckedIn Then
                                                   Finish()
                                               Else
                                                   _player.Position = TimeSpan.Zero
                                                   _player.Play()
                                               End If
                                           End Sub
            AddHandler _player.MediaFailed, Sub(s, e)
                                                _VideoPlaying = False
                                                _safety.Interval = 9000
                                            End Sub
            _host = New ElementHost() With {.Dock = DockStyle.Fill, .BackColor = Color.Black, .Child = _player}
            Controls.Add(_host)
            _host.SendToBack()
            card.BringToFront()
            _player.Source = New Uri(clip)
            _player.Play()
        Catch
            ' No WPF media stack on this PC — fall back to the still image.
            _VideoPlaying = False
            _safety.Interval = 9000
        End Try
    End Sub

    Private Sub Finish()
        If _closing Then Return
        _closing = True
        Close()
    End Sub

    ''' Skips are only honoured once she's checked in — before that, clicking,
    ''' pressing a key, the clip ending or the safety timeout all do nothing.
    Private Sub TryFinish()
        If Not CheckedIn Then Return
        Finish()
    End Sub

    ''' Name plate over the clip (and the fallback photo when there's no video).
    Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
        MyBase.OnPaintBackground(e)
        If _host IsNot Nothing Then Return
        Dim photo = AppPaths.Asset("landing.jpg")
        If File.Exists(photo) Then
            Try
                Using img = Image.FromFile(photo)
                    Dim scale = Math.Max(Width / CSng(img.Width), Height / CSng(img.Height))
                    Dim w = img.Width * scale, h = img.Height * scale
                    e.Graphics.DrawImage(img, (Width - w) / 2, (Height - h) / 2, w, h)
                End Using
            Catch
            End Try
        End If
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        MyBase.OnPaint(e)
        Dim g = e.Graphics
        g.TextRenderingHint = Drawing.Text.TextRenderingHint.ClearTypeGridFit
        Dim y = CInt(Height * 0.72)
        Using shade As New SolidBrush(Color.FromArgb(120, 0, 0, 0))
            g.FillRectangle(shade, 0, y - 40, Width, 200)
        End Using
        Using big As New Font("Segoe UI", 34, FontStyle.Bold),
              small As New Font("Segoe UI", 13),
              white As New SolidBrush(Color.White),
              soft As New SolidBrush(Color.FromArgb(210, 255, 255, 255)),
              centre As New StringFormat() With {.Alignment = StringAlignment.Center}
            g.DrawString($"Welcome, {_name}", big, white, New RectangleF(0, y, Width, 60), centre)
            g.DrawString(AppInfo.CompanyName & "  ·  " & Theme.AppName, small, soft, New RectangleF(0, y + 58, Width, 30), centre)
            If _showHint Then
                Dim hintText = If(CheckedIn, "Click anywhere to continue", "Please check in to continue")
                g.DrawString(hintText, small, soft, New RectangleF(0, y + 96, Width, 30), centre)
            End If
        End Using
    End Sub

End Class
