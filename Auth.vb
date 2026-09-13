Imports System.Data

''' Sign-in policy: PBKDF2 verification, failed-attempt lockout, forced first
''' password change, and a login audit trail. All login paths go through here.
Public Module Auth

    Public Enum LoginOutcome
        Success
        MustChangePassword   ' credentials OK but a real password must be set now
        BadPassword
        UnknownUser
        Disabled
        LockedOut
    End Enum

    Public Class LoginResult
        Public Property Outcome As LoginOutcome
        Public Property UserID As Integer
        Public Property FullName As String
        Public Property Role As String
        Public Property Message As String
    End Class

    ''' A database is "usable" for login only if at least one active Admin has a
    ''' real (pbkdf2) password. Otherwise Program runs the first-admin setup.
    Public Function HasUsableAdmin() As Boolean
        Try
            Dim t = DataAccess.GetTable(
                "SELECT u.PasswordHash FROM Users u JOIN Roles r ON r.RoleID = u.RoleID " &
                "WHERE r.RoleName = 'Admin' AND u.IsActive = 1")
            Return t.Rows.Cast(Of DataRow)().Any(Function(r) Security.IsRealHash(Convert.ToString(r("PasswordHash"))))
        Catch
            Return False
        End Try
    End Function

    Public Function TryLogin(username As String, password As String) As LoginResult
        username = If(username, "").Trim()
        Dim res As New LoginResult()

        Dim t As DataTable
        Try
            t = DataAccess.GetTable(
                "SELECT u.UserID, u.FullName, u.PasswordHash, u.IsActive, u.FailedAttempts, u.LockedUntil, u.MustChangePassword, r.RoleName " &
                "FROM Users u JOIN Roles r ON r.RoleID = u.RoleID WHERE u.Username = @u",
                New Dictionary(Of String, Object) From {{"@u", username}})
        Catch ex As Exception
            res.Outcome = LoginOutcome.UnknownUser
            res.Message = "Could not reach the database. Check the connection on the login screen."
            Return res
        End Try

        If t.Rows.Count = 0 Then
            Audit(username, False, "unknown user")
            res.Outcome = LoginOutcome.UnknownUser
            res.Message = "Unknown username."
            Return res
        End If

        Dim row = t.Rows(0)
        Dim userId = CInt(row("UserID"))
        res.UserID = userId
        res.FullName = Convert.ToString(row("FullName"))
        res.Role = Convert.ToString(row("RoleName"))

        If Not CBool(row("IsActive")) Then
            Audit(username, False, "disabled")
            res.Outcome = LoginOutcome.Disabled
            res.Message = "This account is disabled."
            Return res
        End If

        If row("LockedUntil") IsNot DBNull.Value AndAlso Convert.ToDateTime(row("LockedUntil")) > DateTime.UtcNow Then
            Dim mins = CInt(Math.Ceiling((Convert.ToDateTime(row("LockedUntil")) - DateTime.UtcNow).TotalMinutes))
            Audit(username, False, "locked")
            res.Outcome = LoginOutcome.LockedOut
            res.Message = $"Too many wrong passwords. Try again in {mins} minute(s)."
            Return res
        End If

        Dim stored = Convert.ToString(row("PasswordHash"))

        ' First-time / reset accounts: no real hash yet. Accept the sign-in only
        ' so the app can immediately force a real password to be chosen.
        If Not Security.IsRealHash(stored) Then
            res.Outcome = LoginOutcome.MustChangePassword
            res.Message = "First sign-in — set a password for this account."
            Return res
        End If

        If Security.VerifyPassword(password, stored) Then
            DataAccess.Execute("UPDATE Users SET FailedAttempts = 0, LockedUntil = NULL, LastLoginAt = SYSUTCDATETIME() WHERE UserID = @id",
                New Dictionary(Of String, Object) From {{"@id", userId}})
            Audit(username, True, Nothing)
            If CBool(row("MustChangePassword")) Then
                res.Outcome = LoginOutcome.MustChangePassword
                res.Message = "Your password must be changed before continuing."
            Else
                res.Outcome = LoginOutcome.Success
            End If
            Return res
        End If

        ' Wrong password — bump the counter, lock if over the limit.
        Dim fails = CInt(row("FailedAttempts")) + 1
        If fails >= Security.MaxFailedAttempts Then
            DataAccess.Execute("UPDATE Users SET FailedAttempts = @f, LockedUntil = DATEADD(MINUTE, @m, SYSUTCDATETIME()) WHERE UserID = @id",
                New Dictionary(Of String, Object) From {{"@f", fails}, {"@m", CInt(Security.LockoutWindow.TotalMinutes)}, {"@id", userId}})
            Audit(username, False, "locked after " & fails & " fails")
            res.Outcome = LoginOutcome.LockedOut
            res.Message = $"Too many wrong passwords. Locked for {CInt(Security.LockoutWindow.TotalMinutes)} minutes."
        Else
            DataAccess.Execute("UPDATE Users SET FailedAttempts = @f WHERE UserID = @id",
                New Dictionary(Of String, Object) From {{"@f", fails}, {"@id", userId}})
            Audit(username, False, "bad password")
            res.Outcome = LoginOutcome.BadPassword
            res.Message = $"Incorrect password. {Security.MaxFailedAttempts - fails} attempt(s) left."
        End If
        Return res
    End Function

    ''' Set a real password and clear the must-change flag / lockout.
    Public Sub SetPassword(userId As Integer, newPassword As String)
        DataAccess.Execute(
            "UPDATE Users SET PasswordHash = @h, MustChangePassword = 0, FailedAttempts = 0, LockedUntil = NULL WHERE UserID = @id",
            New Dictionary(Of String, Object) From {{"@h", Security.HashPassword(newPassword)}, {"@id", userId}})
    End Sub

    ''' Create a new account. Returns the new UserID.
    Public Function CreateAccount(fullName As String, username As String, password As String, roleName As String) As Integer
        Return DataAccess.ExecuteScalarInsert(
            "INSERT INTO Users (FullName, Username, PasswordHash, RoleID, IsActive, MustChangePassword) " &
            "VALUES (@n, @u, @h, (SELECT RoleID FROM Roles WHERE RoleName = @r), 1, 0)",
            New Dictionary(Of String, Object) From {
                {"@n", fullName.Trim()}, {"@u", username.Trim()},
                {"@h", Security.HashPassword(password)}, {"@r", roleName}})
    End Function

    Public Function UsernameTaken(username As String) As Boolean
        Return DataAccess.GetTable("SELECT 1 FROM Users WHERE Username = @u",
            New Dictionary(Of String, Object) From {{"@u", username.Trim()}}).Rows.Count > 0
    End Function

    Private Sub Audit(username As String, ok As Boolean, reason As String)
        Try
            DataAccess.Execute(
                "INSERT INTO LoginAudit (Username, Succeeded, Reason, MachineName) VALUES (@u, @s, @r, @m)",
                New Dictionary(Of String, Object) From {
                    {"@u", username}, {"@s", ok}, {"@r", If(reason, CObj(DBNull.Value))}, {"@m", Environment.MachineName}})
        Catch
        End Try
    End Sub

End Module
