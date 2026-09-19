Imports System.Runtime.InteropServices
Imports System.Windows.Forms

''' Watches system-wide input idle time (mouse + keyboard, anywhere on the PC)
''' and raises WentIdle once the session has been idle past the threshold.
''' It then stops; the caller shows the countdown warning and calls Start()
''' again if the user chooses to stay signed in.
''' frmMain starts one of these ONLY for Admin sessions.
Public NotInheritable Class IdleWatcher
    Implements IDisposable

    <StructLayout(LayoutKind.Sequential)>
    Private Structure LASTINPUTINFO
        Public cbSize As UInteger
        Public dwTime As UInteger
    End Structure

    <DllImport("user32.dll")>
    Private Shared Function GetLastInputInfo(ByRef plii As LASTINPUTINFO) As Boolean
    End Function

    Private ReadOnly _threshold As TimeSpan
    Private ReadOnly _timer As New Timer() With {.Interval = 5000} ' check every 5s

    ''' Raised once when the session has been idle for the threshold.
    Public Event WentIdle As EventHandler

    Public Sub New(threshold As TimeSpan)
        _threshold = threshold
        AddHandler _timer.Tick, AddressOf Check
    End Sub

    Public Sub Start()
        _timer.Start()
    End Sub

    Public Sub [Stop]()
        _timer.Stop()
    End Sub

    Private Sub Check(sender As Object, e As EventArgs)
        If IdleTime() >= _threshold Then
            _timer.Stop()
            RaiseEvent WentIdle(Me, EventArgs.Empty)
        End If
    End Sub

    Public Shared Function IdleTime() As TimeSpan
        Dim lii As New LASTINPUTINFO() With {.cbSize = CUInt(Marshal.SizeOf(GetType(LASTINPUTINFO)))}
        If Not GetLastInputInfo(lii) Then Return TimeSpan.Zero
        Dim idleMs = Environment.TickCount - CLng(lii.dwTime)
        If idleMs < 0 Then idleMs = 0
        Return TimeSpan.FromMilliseconds(idleMs)
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        _timer.Stop()
        _timer.Dispose()
    End Sub

End Class
