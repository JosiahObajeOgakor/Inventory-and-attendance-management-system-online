Imports System.Runtime.InteropServices
Imports System.Windows.Forms

''' Watches system-wide input idle time (mouse + keyboard, anywhere on the PC)
''' and raises Expired once the session has been idle past the timeout.
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

    Private ReadOnly _timeout As TimeSpan
    Private ReadOnly _warnAt As TimeSpan
    Private ReadOnly _timer As New Timer() With {.Interval = 15000} ' check every 15s
    Private _warned As Boolean

    ''' Raised when the session has been idle longer than the timeout.
    Public Event Expired As EventHandler
    ''' Raised ~1 minute before expiry so the UI can warn the user.
    Public Event Warning As EventHandler(Of TimeSpan)

    Public Sub New(timeout As TimeSpan)
        _timeout = timeout
        _warnAt = If(timeout > TimeSpan.FromMinutes(2), timeout - TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(timeout.TotalSeconds * 0.8))
        AddHandler _timer.Tick, AddressOf Check
    End Sub

    Public Sub Start()
        _timer.Start()
    End Sub

    Private Sub Check(sender As Object, e As EventArgs)
        Dim idle = IdleTime()
        If idle >= _timeout Then
            _timer.Stop()
            RaiseEvent Expired(Me, EventArgs.Empty)
        ElseIf idle >= _warnAt AndAlso Not _warned Then
            _warned = True
            RaiseEvent Warning(Me, _timeout - idle)
        ElseIf idle < _warnAt Then
            _warned = False
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
