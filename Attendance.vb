Imports System.Data

''' Clerk attendance: check-in (from the card shown while the welcome clip
''' plays) and check-out (sign-out, idle logout, or closing the app). One row
''' per user per day — the first check-in of the day is kept, and the latest
''' check-out overwrites the day's row, so it always ends with the actual
''' first-in / last-out for that day.
Public Module Attendance

    ''' Today's check-in for this user, or Nothing if she hasn't checked in yet.
    Public Function TodayCheckIn(userId As Integer) As DateTime?
        Dim t = DataAccess.GetTable(
            "SELECT CheckInAt FROM Attendance WHERE UserID = @u AND WorkDate = @d",
            New Dictionary(Of String, Object) From {{"@u", userId}, {"@d", Date.Today}})
        If t.Rows.Count = 0 OrElse t.Rows(0)(0) Is DBNull.Value Then Return Nothing
        Return Convert.ToDateTime(t.Rows(0)(0))
    End Function

    ''' Records "checked in now" for today, unless already checked in — then
    ''' returns the existing time untouched. Returns the check-in time either way.
    Public Function CheckIn(userId As Integer, fullName As String) As DateTime
        Dim existing = TodayCheckIn(userId)
        If existing.HasValue Then Return existing.Value

        ' Captured here — the clerk's own PC clock — not the database server's,
        ' so the logged time always matches the moment she actually clicked.
        Dim now = DateTime.Now
        DataAccess.Execute(
            "MERGE Attendance AS t USING (SELECT @u AS UserID, @d AS WorkDate) AS s " &
            "  ON t.UserID = s.UserID AND t.WorkDate = s.WorkDate " &
            "WHEN MATCHED AND t.CheckInAt IS NULL THEN UPDATE SET CheckInAt = @at, FullName = @name " &
            "WHEN NOT MATCHED THEN INSERT (UserID, FullName, WorkDate, CheckInAt) VALUES (@u, @name, @d, @at);",
            New Dictionary(Of String, Object) From {{"@u", userId}, {"@name", fullName}, {"@d", Date.Today}, {"@at", now}})

        Return If(TodayCheckIn(userId), now)
    End Function

    ''' Records "checked out now" for today — only when there's an open check-in
    ''' (so signing out on a day she never checked in writes nothing).
    Public Sub CheckOut(userId As Integer)
        Try
            DataAccess.Execute(
                "UPDATE Attendance SET CheckOutAt = @at " &
                "WHERE UserID = @u AND WorkDate = @d AND CheckInAt IS NOT NULL",
                New Dictionary(Of String, Object) From {{"@u", userId}, {"@d", Date.Today}, {"@at", DateTime.Now}})
        Catch
            ' Never let attendance logging get in the way of signing out.
        End Try
    End Sub

    ''' She chose not to check in and signed straight back out from the welcome
    ''' screen — still logged (CheckInAt stays NULL) so admin can see she signed
    ''' in and out without clocking in, and when.
    Public Sub DeclineAndSignOut(userId As Integer, fullName As String)
        Try
            DataAccess.Execute(
                "MERGE Attendance AS t USING (SELECT @u AS UserID, @d AS WorkDate) AS s " &
                "  ON t.UserID = s.UserID AND t.WorkDate = s.WorkDate " &
                "WHEN MATCHED THEN UPDATE SET CheckOutAt = @at " &
                "WHEN NOT MATCHED THEN INSERT (UserID, FullName, WorkDate, CheckOutAt) VALUES (@u, @name, @d, @at);",
                New Dictionary(Of String, Object) From {{"@u", userId}, {"@name", fullName}, {"@d", Date.Today}, {"@at", DateTime.Now}})
        Catch
            ' Never let attendance logging get in the way of signing out.
        End Try
    End Sub

    ''' Today's check-in/out for this user, for her own Dashboard.
    Public Function TodayRecord(userId As Integer) As DataRow
        Dim t = DataAccess.GetTable(
            "SELECT CheckInAt, CheckOutAt FROM Attendance WHERE UserID = @u AND WorkDate = @d",
            New Dictionary(Of String, Object) From {{"@u", userId}, {"@d", Date.Today}})
        Return If(t.Rows.Count > 0, t.Rows(0), Nothing)
    End Function

    ''' Attendance rows for the admin's Attendance screen, newest day first.
    Public Function ForDateRange([from] As Date, [to] As Date) As DataTable
        Return DataAccess.GetTable(
            "SELECT FullName, WorkDate, CheckInAt, CheckOutAt " &
            "FROM Attendance WHERE WorkDate BETWEEN @f AND @t " &
            "ORDER BY WorkDate DESC, FullName",
            New Dictionary(Of String, Object) From {{"@f", [from].Date}, {"@t", [to].Date}})
    End Function

End Module
