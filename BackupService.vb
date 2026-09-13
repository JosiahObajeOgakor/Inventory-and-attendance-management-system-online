Imports System.IO

''' Automatic backups. The first time the app is used each day it writes a full
''' database backup into %LOCALAPPDATA%\StockDesk\Backups and keeps the most
''' recent ones, so a manual "Backup database…" is never the only copy.
'''
''' These live on the same PC, so they protect against mistakes and corruption —
''' not against the machine being lost or stolen. Copy the folder to a USB stick
''' or cloud drive regularly; the archive screen says so too.
Public Module BackupService

    Public Const KeepCount As Integer = 14
    Private Const LastRunKey As String = "backup.lastDate"

    Public Function BackupFolder() As String
        Return Path.Combine(Path.GetDirectoryName(DbBootstrap.DataDirectory().TrimEnd(Path.DirectorySeparatorChar)), "Backups")
    End Function

    ''' Runs a backup if none has been taken today. Returns the file written, or
    ''' "" when today's backup already exists. Never throws: a failed backup must
    ''' not stop anyone working (it's reported by LastError).
    Public Function RunDailyBackup() As String
        Try
            If Convert.ToString(GetSetting(LastRunKey)) = Date.Today.ToString("yyyy-MM-dd") Then Return ""
            Dim file = BackupNow()
            If file <> "" Then SetSetting(LastRunKey, Date.Today.ToString("yyyy-MM-dd"))
            Return file
        Catch ex As Exception
            LastError = ex.Message
            Return ""
        End Try
    End Function

    Public Property LastError As String = ""

    ''' Takes a backup now and prunes old ones. Returns the file, or "" on failure.
    Public Function BackupNow() As String
        Try
            Dim folder = BackupFolder()
            Directory.CreateDirectory(folder)
            Dim file = Path.Combine(folder, $"ChewyStock_{Date.Now:yyyyMMdd_HHmmss}.bak")
            Dim err = DbBootstrap.BackupTo(file)
            If err <> "" Then
                LastError = err
                Return ""
            End If
            Prune(folder)
            LastError = ""
            Return file
        Catch ex As Exception
            LastError = ex.Message
            Return ""
        End Try
    End Function

    ''' Keeps the newest KeepCount backups and deletes the rest.
    Private Sub Prune(folder As String)
        Try
            Dim old = New DirectoryInfo(folder).GetFiles("ChewyStock_*.bak").
                OrderByDescending(Function(f) f.LastWriteTimeUtc).Skip(KeepCount).ToList()
            For Each f In old
                Try
                    f.Delete()
                Catch
                End Try
            Next
        Catch
        End Try
    End Sub

    ''' Newest backup on this PC, or Nothing when there are none yet.
    Public Function LatestBackup() As FileInfo
        Try
            Return New DirectoryInfo(BackupFolder()).GetFiles("ChewyStock_*.bak").
                OrderByDescending(Function(f) f.LastWriteTimeUtc).FirstOrDefault()
        Catch
            Return Nothing
        End Try
    End Function

    Private Function GetSetting(key As String) As String
        Try
            Dim t = DataAccess.GetTable("SELECT SettingValue FROM AppSettings WHERE SettingKey = @k",
                                        New Dictionary(Of String, Object) From {{"@k", key}})
            Return If(t.Rows.Count > 0, Convert.ToString(t.Rows(0)(0)), "")
        Catch
            Return ""
        End Try
    End Function

    Private Sub SetSetting(key As String, value As String)
        DataAccess.Execute(
            "MERGE AppSettings AS t USING (SELECT @k AS k, @v AS v) s ON t.SettingKey = s.k " &
            "WHEN MATCHED THEN UPDATE SET SettingValue = s.v " &
            "WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue) VALUES (s.k, s.v);",
            New Dictionary(Of String, Object) From {{"@k", key}, {"@v", value}})
    End Sub

End Module
