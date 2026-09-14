Imports System.Data

''' Sign-in policy: PBKDF2 verification, failed-attempt lockout, forced first
''' password change, and a login audit trail. All login paths go through here.
'''
''' Accounts are shared by both businesses (see Company): they are checked and
''' changed in the home database only, then mirrored into whichever business is
''' signed into, because that business's own records (sales, attendance…) point
''' at a user row in its own database.
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
        Public Property Username As String
        Public Property FullName As String
        Public Property Role As String
        Public Property Message As String
    End Class

    ''' A database is "usable" for login only if at least one active Admin has a
    ''' real (pbkdf2) password. Otherwise Program runs the first-admin setup.
    Public Function HasUsableAdmin() As Boolean
        Try
            Dim t = DataAccess.AccountsTable(
                "SELECT u.PasswordHash FROM Users u JOIN Roles r ON r.RoleID = u.RoleID " &
                "WHERE r.RoleName = 'Admin' AND u.IsActive = 1")
            Return t.Rows.Cast(Of DataRow)().Any(Function(r) Security.IsRealHash(Convert.ToString(r("PasswordHash"))))
        Catch
            Return False
        End Try
    End Function

    ''' Checks the credentials against the shared accounts. The UserID returned
    ''' is the home database's; EnterCompany swaps it for the signed-into one.
    Public Function TryLogin(username As String, password As String) As LoginResult
        username = If(username, "").Trim()
        Dim res As New LoginResult() With {.Username = username}

        Dim t As DataTable
        Try
            t = DataAccess.AccountsTable(
                "SELECT u.UserID, u.Username, u.FullName, u.PasswordHash, u.IsActive, u.FailedAttempts, u.LockedUntil, u.MustChangePassword, r.RoleName " &
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
        res.Username = Convert.ToString(row("Username"))
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
            DataAccess.AccountsExecute("UPDATE Users SET FailedAttempts = 0, LockedUntil = NULL, LastLoginAt = SYSUTCDATETIME() WHERE UserID = @id",
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
            DataAccess.AccountsExecute("UPDATE Users SET FailedAttempts = @f, LockedUntil = DATEADD(MINUTE, @m, SYSUTCDATETIME()) WHERE UserID = @id",
                New Dictionary(Of String, Object) From {{"@f", fails}, {"@m", CInt(Security.LockoutWindow.TotalMinutes)}, {"@id", userId}})
            Audit(username, False, "locked after " & fails & " fails")
            res.Outcome = LoginOutcome.LockedOut
            res.Message = $"Too many wrong passwords. Locked for {CInt(Security.LockoutWindow.TotalMinutes)} minutes."
        Else
            DataAccess.AccountsExecute("UPDATE Users SET FailedAttempts = @f WHERE UserID = @id",
                New Dictionary(Of String, Object) From {{"@f", fails}, {"@id", userId}})
            Audit(username, False, "bad password")
            res.Outcome = LoginOutcome.BadPassword
            res.Message = $"Incorrect password. {Security.MaxFailedAttempts - fails} attempt(s) left."
        End If
        Return res
    End Function

    ''' Opens `company` for a signed-in account: switches the app to it, creates
    ''' its database on first use, and makes sure the account exists there with
    ''' its current name, role and password. Rewrites result.UserID to that
    ''' database's id. Returns "" on success, else a message for the sign-in screen.
    Public Function EnterCompany(result As LoginResult, company As Company) As String
        Company.Use(company)
        If Not company.IsHome Then
            Dim err = DbBootstrap.EnsureDatabase()
            If err = "" Then err = DbBootstrap.Migrate()
            If err = "" Then err = CompanyData.Prepare(company)
            If err <> "" Then
                Company.Use(Company.Home)
                Return $"{company.DisplayName} could not be opened: {err}"
            End If
        End If
        Try
            result.UserID = MirrorAccount(result.Username)
        Catch ex As Exception
            Company.Use(Company.Home)
            Return $"Your account could not be opened in {company.DisplayName}: {ex.Message}"
        End Try
        Company.Remember(company)
        Return ""
    End Function

    ''' Copies the shared account into the signed-into business's database and
    ''' returns its id there. In the home database it's the same row, untouched.
    Friend Function MirrorAccount(username As String) As Integer
        If Company.Current.IsHome Then
            Return Convert.ToInt32(DataAccess.GetTable("SELECT UserID FROM Users WHERE Username = @u",
                New Dictionary(Of String, Object) From {{"@u", username}}).Rows(0)(0))
        End If
        Dim account = DataAccess.AccountsTable(
            "SELECT u.Username, u.FullName, u.PasswordHash, u.IsActive, u.MustChangePassword, r.RoleName " &
            "FROM Users u JOIN Roles r ON r.RoleID = u.RoleID WHERE u.Username = @u",
            New Dictionary(Of String, Object) From {{"@u", username}}).Rows(0)
        Dim p As New Dictionary(Of String, Object) From {
            {"@u", account("Username")}, {"@n", account("FullName")}, {"@h", account("PasswordHash")},
            {"@a", account("IsActive")}, {"@m", account("MustChangePassword")}, {"@r", account("RoleName")}}
        DataAccess.Execute(
            "MERGE Users AS t USING (SELECT @u AS Username) s ON t.Username = s.Username " &
            "WHEN MATCHED THEN UPDATE SET FullName = @n, PasswordHash = @h, IsActive = @a, MustChangePassword = @m, " &
            "    RoleID = (SELECT RoleID FROM Roles WHERE RoleName = @r), LastLoginAt = SYSUTCDATETIME() " &
            "WHEN NOT MATCHED THEN INSERT (FullName, Username, PasswordHash, RoleID, IsActive, MustChangePassword, LastLoginAt) " &
            "    VALUES (@n, @u, @h, (SELECT RoleID FROM Roles WHERE RoleName = @r), @a, @m, SYSUTCDATETIME());", p)
        Return Convert.ToInt32(DataAccess.GetTable("SELECT UserID FROM Users WHERE Username = @u",
            New Dictionary(Of String, Object) From {{"@u", username}}).Rows(0)(0))
    End Function

    ''' The username behind a user id of the business signed into.
    Private Function UsernameOf(userId As Integer) As String
        Dim t = DataAccess.GetTable("SELECT Username FROM Users WHERE UserID = @id",
            New Dictionary(Of String, Object) From {{"@id", userId}})
        Return If(t.Rows.Count = 0, Nothing, Convert.ToString(t.Rows(0)(0)))
    End Function

    ''' The shared password hash for a user id of the business signed into.
    Public Function StoredHash(userId As Integer) As String
        Dim username = UsernameOf(userId)
        If username Is Nothing Then Return ""
        Dim t = DataAccess.AccountsTable("SELECT PasswordHash FROM Users WHERE Username = @u",
            New Dictionary(Of String, Object) From {{"@u", username}})
        Return If(t.Rows.Count = 0, "", Convert.ToString(t.Rows(0)(0)))
    End Function

    ''' Set a real password and clear the must-change flag / lockout. `userId`
    ''' is an id in the business signed into; the change applies to both.
    Public Sub SetPassword(userId As Integer, newPassword As String)
        Dim username = UsernameOf(userId)
        If username Is Nothing Then Throw New InvalidOperationException("That account no longer exists.")
        Dim p As New Dictionary(Of String, Object) From {{"@h", Security.HashPassword(newPassword)}, {"@u", username}}
        Const Sql = "UPDATE Users SET PasswordHash = @h, MustChangePassword = 0, FailedAttempts = 0, LockedUntil = NULL WHERE Username = @u"
        DataAccess.AccountsExecute(Sql, p)
        If Not Company.Current.IsHome Then DataAccess.Execute(Sql, p)
    End Sub

    ''' Create a new account. Returns its UserID in the home database.
    Public Function CreateAccount(fullName As String, username As String, password As String, roleName As String) As Integer
        Return AddAccount(fullName, username, Security.HashPassword(password), roleName, mustChange:=False)
    End Function

    ''' Adds an account to the shared list; it appears in the other business the
    ''' first time its owner signs in there.
    Public Function AddAccount(fullName As String, username As String, passwordHash As String,
                               roleName As String, mustChange As Boolean) As Integer
        Dim t = DataAccess.AccountsTable(
            "INSERT INTO Users (FullName, Username, PasswordHash, RoleID, IsActive, MustChangePassword) " &
            "OUTPUT INSERTED.UserID " &
            "VALUES (@n, @u, @h, (SELECT RoleID FROM Roles WHERE RoleName = @r), 1, @m)",
            New Dictionary(Of String, Object) From {
                {"@n", fullName.Trim()}, {"@u", username.Trim()},
                {"@h", passwordHash}, {"@r", roleName}, {"@m", mustChange}})
        Return Convert.ToInt32(t.Rows(0)(0))
    End Function

    Public Function UsernameTaken(username As String) As Boolean
        Return DataAccess.AccountsTable("SELECT 1 FROM Users WHERE Username = @u",
            New Dictionary(Of String, Object) From {{"@u", username.Trim()}}).Rows.Count > 0
    End Function

    ''' Every shared account, for the staff screen.
    Public Function Accounts() As DataTable
        Return DataAccess.AccountsTable(
            "SELECT u.Username, u.FullName, r.RoleName, " &
            "CASE WHEN u.IsActive = 1 THEN 'Active' ELSE 'Disabled' END AS StatusLabel " &
            "FROM Users u JOIN Roles r ON r.RoleID = u.RoleID ORDER BY u.FullName")
    End Function

    ''' Enable/disable an account for both businesses. Sign-in checks the shared
    ''' account, so a disabled user is kept out of either one straight away.
    Public Sub ToggleActive(username As String)
        Dim p As New Dictionary(Of String, Object) From {{"@u", username}}
        DataAccess.AccountsExecute("UPDATE Users SET IsActive = 1 - IsActive WHERE Username = @u", p)
        If Not Company.Current.IsHome Then DataAccess.Execute("UPDATE Users SET IsActive = 1 - IsActive WHERE Username = @u", p)
    End Sub

    ''' Removes a shared account. Its copy in the other business stays only as
    ''' history for records it made there — nobody can sign in with it.
    Public Function DeleteAccount(username As String) As Integer
        Return DataAccess.AccountsExecute("DELETE FROM Users WHERE Username = @u",
            New Dictionary(Of String, Object) From {{"@u", username}})
    End Function

    Private Sub Audit(username As String, ok As Boolean, reason As String)
        Try
            DataAccess.AccountsExecute(
                "INSERT INTO LoginAudit (Username, Succeeded, Reason, MachineName) VALUES (@u, @s, @r, @m)",
                New Dictionary(Of String, Object) From {
                    {"@u", username}, {"@s", ok}, {"@r", If(reason, CObj(DBNull.Value))}, {"@m", Environment.MachineName}})
        Catch
        End Try
    End Sub

End Module
