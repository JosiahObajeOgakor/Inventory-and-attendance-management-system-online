Imports System.IO
Imports System.Windows.Forms

''' Last line of defence. Anything that escapes a screen's own error handling is
''' written to %LOCALAPPDATA%\StockDesk\Logs and shown as a plain message, rather
''' than closing the app with a Windows error box. Saved data is never at risk —
''' every save is a single transaction — so in most cases work can carry on.
Public Module CrashLog

    Public Function LogFolder() As String
        Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StockDesk", "Logs")
    End Function

    ''' Wires the handlers. Called once at start-up.
    Public Sub Install()
        AddHandler Application.ThreadException,
            Sub(s, e)
                Dim file = Write(e.Exception, "UI")
                Report(e.Exception, file, fatal:=False)
            End Sub
        AddHandler AppDomain.CurrentDomain.UnhandledException,
            Sub(s, e)
                Dim ex = TryCast(e.ExceptionObject, Exception)
                Dim file = Write(ex, "background")
                Report(ex, file, fatal:=True)
            End Sub
    End Sub

    ''' Appends the failure to today's log; returns the file path ("" if even
    ''' logging failed — never throws).
    Public Function Write(ex As Exception, where As String) As String
        Try
            Directory.CreateDirectory(LogFolder())
            Dim logPath = Path.Combine(LogFolder(), $"crash-{Date.Today:yyyyMMdd}.log")
            File.AppendAllText(logPath,
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({where}) ---" & vbCrLf &
                If(ex Is Nothing, "Unknown error", ex.ToString()) & vbCrLf & vbCrLf)
            Return logPath
        Catch
            Return ""
        End Try
    End Function

    Private Sub Report(ex As Exception, logFile As String, fatal As Boolean)
        Try
            Dim message = "Something went wrong:" & vbCrLf & vbCrLf &
                          If(ex Is Nothing, "Unknown error", ex.Message) & vbCrLf & vbCrLf &
                          If(fatal,
                             "ChewyStock has to close. Your saved records are safe — every sale, purchase and production entry is saved in one step.",
                             "Your saved records are safe. You can carry on, but save your work and restart ChewyStock when convenient.")
            If logFile <> "" Then message &= vbCrLf & vbCrLf & "Details for support: " & logFile
            MessageBox.Show(message, Theme.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning)
        Catch
        End Try
    End Sub

End Module
