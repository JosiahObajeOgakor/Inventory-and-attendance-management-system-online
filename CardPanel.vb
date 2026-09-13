Imports System.Windows.Forms
Imports System.Drawing
Imports System.Drawing.Drawing2D

''' A panel drawn as a rounded "card" with a soft drop shadow — used to lift the
''' nav bar's tab strip off the page so it reads as one modern control instead of
''' a flat coloured band.
Public Class CardPanel
    Inherits Panel

    Public Property CardColor As Color = Color.White
    Public Property CornerRadius As Integer = 10
    Private Const ShadowSize As Integer = 6

    Public Sub New()
        SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.UserPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw, True)
        ' Room for the shadow inside the control's own bounds — Padding already
        ' reserves space for real content, so children never sit under it.
        Margin = New Padding(0, 0, 0, ShadowSize)
    End Sub

    Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
        ' Painted fully in OnPaint instead — avoids a flat-colour flash before the card draws.
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim g = e.Graphics
        g.SmoothingMode = SmoothingMode.AntiAlias
        Dim parentBg = If(Parent IsNot Nothing, Parent.BackColor, BackColor)
        Using bg As New SolidBrush(parentBg)
            g.FillRectangle(bg, ClientRectangle)
        End Using

        Dim body As New Rectangle(0, 0, Width, Height - ShadowSize)
        If body.Width <= 0 OrElse body.Height <= 0 Then Return

        If Not Theme.ReducedEffects Then
            Using shadowPath = RoundedPath(New Rectangle(2, ShadowSize, body.Width - 4, body.Height - 2), CornerRadius)
                For i = ShadowSize To 1 Step -1
                    Using pen As New Pen(Color.FromArgb(CInt(10 + i * 3), 20, 20, 30), 1)
                        Dim r As New Rectangle(body.X, body.Y + i, body.Width - 1, body.Height - 1)
                        Using p2 = RoundedPath(r, CornerRadius)
                            g.DrawPath(pen, p2)
                        End Using
                    End Using
                Next
            End Using
        End If

        Using cardPath = RoundedPath(New Rectangle(0, 0, body.Width - 1, body.Height - 1), CornerRadius)
            Using b As New SolidBrush(CardColor)
                g.FillPath(b, cardPath)
            End Using
            Using pen As New Pen(Color.FromArgb(24, 0, 0, 0))
                g.DrawPath(pen, cardPath)
            End Using
        End Using

        MyBase.OnPaint(e)
    End Sub

    Private Shared Function RoundedPath(r As Rectangle, radius As Integer) As GraphicsPath
        Dim d = radius * 2
        Dim p As New GraphicsPath()
        If d >= r.Width Then d = Math.Max(2, r.Width - 1)
        If d >= r.Height Then d = Math.Max(2, r.Height - 1)
        p.AddArc(r.X, r.Y, d, d, 180, 90)
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90)
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90)
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90)
        p.CloseFigure()
        Return p
    End Function

End Class
