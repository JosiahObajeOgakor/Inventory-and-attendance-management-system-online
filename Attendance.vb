Imports System.Data

''' Clerk attendance, as an append-only event log.
'''
''' It used to be one row per user per day, and CheckIn returned early if that
''' row already existed — so the first check-in of a day was the only one ever
''' written. A clerk who signed in again after lunch clicked the button, saw it
''' work, and the database still showed the morning's time. That is why the time
''' looked hard-coded: it was, in effect, frozen at the first write of the day.
'''
''' Now every check-in, check-out and declined check-in appends its own row,
''' stamped with the moment it happened. Nothing is ever overwritten, so the
''' admin's screen is a real audit trail rather than a daily summary, and a time
''' on it is always the time that action actually occurred.
'''
''' The timestamp is taken with DateTime.Now on the machine the clerk is sitting
''' at, at the instant of the click — not a database default, which would record
''' whenever the row happened to be written. WorkDate is a computed column
''' derived from that same instant, so the date and the time can never disagree
''' (a check-in at 23:59 lands on the right day by construction).
Public Module Attendance

    Public Const CheckedIn As String = "In"
    Public Const CheckedOut As String = "Out"
    Public Const Declined As String = "Declined"

    ''' Appends one event. Returns the exact moment recorded.
    Private Function Log(userId As Integer, fullName As String, eventType As String) As DateTime
        Dim happenedAt = DateTime.Now
        DataAccess.Execute(
            "INSERT INTO AttendanceEvents (UserID, FullName, EventType, HappenedAt) VALUES (@u, @n, @e, @at)",
            New Dictionary(Of String, Object) From {
                {"@u", userId}, {"@n", fullName}, {"@e", eventType}, {"@at", happenedAt}})
        Return happenedAt
    End Function

    ''' Records a check-in at this moment. Always writes — a clerk who signs in
    ''' three times in a day is checked in three times, and all three are kept.
    Public Function CheckIn(userId As Integer, fullName As String) As DateTime
        Return Log(userId, fullName, CheckedIn)
    End Function

    ''' Records a check-out at this moment. Only meaningful after a check-in, but
    ''' never throws: attendance logging must not block anyone signing out.
    Public Sub CheckOut(userId As Integer)
        Try
            Log(userId, NameOf_(userId), CheckedOut)
        Catch
        End Try
    End Sub

    ''' She reached the welcome screen and signed out instead of checking in.
    ''' Logged too, so the admin can see she was here and chose not to clock on.
    Public Sub DeclineAndSignOut(userId As Integer, fullName As String)
        Try
            Log(userId, fullName, Declined)
        Catch
        End Try
    End Sub

    Private Function NameOf_(userId As Integer) As String
        Dim name = DataAccess.GetTable("SELECT FullName FROM Users WHERE UserID = @u",
            New Dictionary(Of String, Object) From {{"@u", userId}})
        Return If(name.Rows.Count > 0, Convert.ToString(name.Rows(0)(0)), "User " & userId)
    End Function

    ''' The first check-in today — when her day started.
    Public Function TodayCheckIn(userId As Integer) As DateTime?
        Return SingleTime(
            "SELECT MIN(HappenedAt) FROM AttendanceEvents " &
            "WHERE UserID = @u AND WorkDate = @d AND EventType = @e", userId, CheckedIn)
    End Function

    ''' The most recent check-in today — what she'd expect to see after clicking
    ''' the button just now.
    Public Function LastCheckIn(userId As Integer) As DateTime?
        Return SingleTime(
            "SELECT MAX(HappenedAt) FROM AttendanceEvents " &
            "WHERE UserID = @u AND WorkDate = @d AND EventType = @e", userId, CheckedIn)
    End Function

    Private Function SingleTime(sql As String, userId As Integer, eventType As String) As DateTime?
        Dim t = DataAccess.GetTable(sql, New Dictionary(Of String, Object) From {
            {"@u", userId}, {"@d", Date.Today}, {"@e", eventType}})
        If t.Rows.Count = 0 OrElse t.Rows(0)(0) Is DBNull.Value Then Return Nothing
        Return Convert.ToDateTime(t.Rows(0)(0))
    End Function

    ''' How many times she has checked in today — the number that makes a second
    ''' or third check-in visible instead of silently lost.
    Public Function CheckInCountToday(userId As Integer) As Integer
        Return Convert.ToInt32(DataAccess.GetTable(
            "SELECT COUNT(*) FROM AttendanceEvents WHERE UserID = @u AND WorkDate = @d AND EventType = @e",
            New Dictionary(Of String, Object) From {
                {"@u", userId}, {"@d", Date.Today}, {"@e", CheckedIn}}).Rows(0)(0))
    End Function

    ''' Her day so far, for her own Dashboard: first in, last out.
    Public Function TodayRecord(userId As Integer) As DataRow
        Dim t = DataAccess.GetTable(
            "SELECT MIN(CASE WHEN EventType = 'In' THEN HappenedAt END) AS CheckInAt, " &
            "       MAX(CASE WHEN EventType = 'Out' THEN HappenedAt END) AS CheckOutAt " &
            "FROM AttendanceEvents WHERE UserID = @u AND WorkDate = @d",
            New Dictionary(Of String, Object) From {{"@u", userId}, {"@d", Date.Today}})
        If t.Rows.Count = 0 OrElse t.Rows(0)("CheckInAt") Is DBNull.Value Then Return Nothing
        Return t.Rows(0)
    End Function

    ''' The audit trail: every event in the window, most recent first. This is
    ''' the admin's view — one line per action, never rolled up, so a day with
    ''' four check-ins reads as four check-ins.
    Public Function ForDateRange([from] As Date, [to] As Date) As DataTable
        Return DataAccess.GetTable(
            "SELECT FullName, WorkDate, HappenedAt AS [At], " &
            "       CASE EventType WHEN 'In' THEN 'Checked in' WHEN 'Out' THEN 'Checked out' " &
            "                      ELSE 'Declined to check in' END AS [Event] " &
            "FROM AttendanceEvents WHERE WorkDate BETWEEN @f AND @t " &
            "ORDER BY HappenedAt DESC",
            New Dictionary(Of String, Object) From {{"@f", [from].Date}, {"@t", [to].Date}})
    End Function

    ''' One line per person per day: first in, last out, and how many times they
    ''' clocked on — for payroll and the at-a-glance read.
    ''' Grouped by UserID, not by name: the name stored on an event is whatever
    ''' was known at the time, so grouping on it would split one person's day in
    ''' two the moment their name were corrected.
    Public Function DailySummary([from] As Date, [to] As Date) As DataTable
        Return DataAccess.GetTable(
            "SELECT MAX(FullName) AS FullName, WorkDate, " &
            "       MIN(CASE WHEN EventType = 'In' THEN HappenedAt END) AS [First in], " &
            "       MAX(CASE WHEN EventType = 'Out' THEN HappenedAt END) AS [Last out], " &
            "       SUM(CASE WHEN EventType = 'In' THEN 1 ELSE 0 END) AS [Check-ins] " &
            "FROM AttendanceEvents WHERE WorkDate BETWEEN @f AND @t " &
            "GROUP BY UserID, WorkDate ORDER BY WorkDate DESC, MAX(FullName)",
            New Dictionary(Of String, Object) From {{"@f", [from].Date}, {"@t", [to].Date}})
    End Function

End Module
