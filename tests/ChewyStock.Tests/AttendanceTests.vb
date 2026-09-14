Imports System.Data
Imports System.Windows.Forms
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Attendance is time-sensitive and must be an audit trail: every check-in kept,
''' each stamped with the moment it happened.
'''
''' The bug these were written for: attendance used to be one row per user per
''' day, and CheckIn returned early when that row existed. A clerk signing in
''' after lunch clicked the button, saw it work, and the database still held the
''' morning's time. Several of these tests fail outright against that design —
''' which is the point of them.
<TestClass>
Public Class AttendanceTests

    Private _userId As Integer

    <TestInitialize>
    Public Sub Setup()
        _userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
        TestDb.Exec("DELETE FROM AttendanceEvents WHERE UserID = @u", TestDb.P("@u", _userId))
    End Sub

    <TestCleanup>
    Public Sub Cleanup()
        TestDb.Exec("DELETE FROM AttendanceEvents WHERE UserID = @u", TestDb.P("@u", _userId))
    End Sub

    Private Function EventsToday() As DataTable
        Return TestDb.Table(
            "SELECT EventType, HappenedAt FROM AttendanceEvents " &
            "WHERE UserID = @u AND WorkDate = @d ORDER BY HappenedAt",
            TestDb.P("@u", _userId, "@d", Date.Today))
    End Function

    ' ===== every check-in is recorded =====

    <TestMethod>
    Public Sub Checking_in_records_the_moment_it_happened()
        Dim before = DateTime.Now.AddSeconds(-2)
        Dim at = Attendance.CheckIn(_userId, "Clock Test")
        Dim after = DateTime.Now.AddSeconds(2)

        Assert.IsTrue(at >= before AndAlso at <= after,
                      $"the logged time {at:HH:mm:ss} must be the real moment of the click")
        ' Within a second: SQL Server rounds a datetime parameter to the nearest
        ' few milliseconds. What matters is that it's this moment and not one
        ' frozen earlier in the day.
        Dim stored = Convert.ToDateTime(EventsToday().Rows(0)("HappenedAt"))
        Assert.IsTrue(Math.Abs((stored - at).TotalSeconds) < 1,
                      $"stored {stored:HH:mm:ss.fff} should be the moment of the click, {at:HH:mm:ss.fff}")
    End Sub

    <TestMethod>
    Public Sub A_second_check_in_on_the_same_day_is_also_recorded()
        ' The whole bug: signing back in after lunch used to write nothing.
        Attendance.CheckIn(_userId, "Clock Test")
        Threading.Thread.Sleep(1100)      ' so the two times are visibly apart
        Attendance.CheckIn(_userId, "Clock Test")

        Dim events = EventsToday()
        Assert.AreEqual(2, events.Rows.Count, "both check-ins have to survive — this is an audit trail")
        Assert.AreNotEqual(Convert.ToDateTime(events.Rows(0)("HappenedAt")),
                           Convert.ToDateTime(events.Rows(1)("HappenedAt")),
                           "and each must carry its own time, not a copy of the first")
    End Sub

    <TestMethod>
    Public Sub The_second_check_in_returns_the_new_time_not_the_first_one()
        Dim first = Attendance.CheckIn(_userId, "Clock Test")
        Threading.Thread.Sleep(1100)
        Dim second = Attendance.CheckIn(_userId, "Clock Test")

        Assert.IsTrue(second > first,
                      $"checking in again at {second:HH:mm:ss} must not report the earlier {first:HH:mm:ss}")
    End Sub

    <TestMethod>
    Public Sub Three_check_ins_in_a_day_are_all_kept_in_order()
        For pass = 1 To 3
            Attendance.CheckIn(_userId, "Clock Test")
            Threading.Thread.Sleep(1100)
        Next

        Dim events = EventsToday()
        Assert.AreEqual(3, events.Rows.Count)
        Assert.AreEqual(3, Attendance.CheckInCountToday(_userId), "the count is what makes repeat check-ins visible")
        For i = 1 To events.Rows.Count - 1
            Assert.IsTrue(Convert.ToDateTime(events.Rows(i)("HappenedAt")) > Convert.ToDateTime(events.Rows(i - 1)("HappenedAt")),
                          "the trail has to read in the order things happened")
        Next
    End Sub

    <TestMethod>
    Public Sub The_first_and_latest_check_in_are_told_apart()
        Dim first = Attendance.CheckIn(_userId, "Clock Test")
        Threading.Thread.Sleep(1100)
        Dim latest = Attendance.CheckIn(_userId, "Clock Test")

        Assert.AreEqual(first.ToString("HH:mm:ss"), Attendance.TodayCheckIn(_userId).Value.ToString("HH:mm:ss"),
                        "when her day started is the first check-in")
        Assert.AreEqual(latest.ToString("HH:mm:ss"), Attendance.LastCheckIn(_userId).Value.ToString("HH:mm:ss"),
                        "what she just did is the latest one")
    End Sub

    ' ===== check-out and declining =====

    <TestMethod>
    Public Sub Checking_out_is_its_own_event_and_never_erases_the_check_in()
        Attendance.CheckIn(_userId, "Clock Test")
        Threading.Thread.Sleep(1100)
        Attendance.CheckOut(_userId)

        Dim events = EventsToday()
        Assert.AreEqual(2, events.Rows.Count)
        Assert.AreEqual("In", Convert.ToString(events.Rows(0)("EventType")))
        Assert.AreEqual("Out", Convert.ToString(events.Rows(1)("EventType")))
        Assert.IsTrue(Attendance.TodayCheckIn(_userId).HasValue, "signing out must not wipe when she arrived")
    End Sub

    <TestMethod>
    Public Sub A_full_day_of_in_and_out_keeps_every_leg()
        ' Morning in, lunch out, afternoon in, evening out.
        Attendance.CheckIn(_userId, "Clock Test") : Threading.Thread.Sleep(1100)
        Attendance.CheckOut(_userId) : Threading.Thread.Sleep(1100)
        Attendance.CheckIn(_userId, "Clock Test") : Threading.Thread.Sleep(1100)
        Attendance.CheckOut(_userId)

        Assert.AreEqual(4, EventsToday().Rows.Count, "a split shift is four events, not one row")
        Assert.AreEqual(2, Attendance.CheckInCountToday(_userId))
    End Sub

    <TestMethod>
    Public Sub Declining_to_check_in_is_logged_too()
        Attendance.DeclineAndSignOut(_userId, "Clock Test")

        Dim events = EventsToday()
        Assert.AreEqual(1, events.Rows.Count)
        Assert.AreEqual("Declined", Convert.ToString(events.Rows(0)("EventType")),
                        "admin should be able to see she was here and chose not to clock on")
        Assert.IsFalse(Attendance.TodayCheckIn(_userId).HasValue, "but it is not a check-in")
    End Sub

    ' ===== what the clerk sees on her way in =====

    <TestMethod>
    Public Sub The_welcome_screen_asks_for_a_check_in_again_on_a_second_login()
        ' The bug this exists for: the screen used to hide the button once a
        ' check-in existed for today, so signing back in after a break recorded
        ' nothing and kept reporting whenever she first arrived.
        Attendance.CheckIn(_userId, "Second Login Clerk")

        Using f As New frmClerkWelcome(_userId, "Second Login Clerk")
            f.Show()
            Application.DoEvents()

            Dim button = DirectCast(FieldOf(f, "btnCheckIn"), Control)
            Assert.IsTrue(button.Visible, "every successful login has to ask for a check-in, not just the day's first")
            Assert.IsTrue(button.Enabled)
            Assert.IsFalse(f.CheckedIn, "she hasn't clicked it yet on this login")
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Checking_in_from_the_welcome_screen_a_second_time_records_a_new_event()
        Dim first = Attendance.CheckIn(_userId, "Repeat Clerk")
        Threading.Thread.Sleep(1100)

        Using f As New frmClerkWelcome(_userId, "Repeat Clerk")
            f.Show()
            Application.DoEvents()
            f.GetType().GetMethod("CheckIn_Click", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).
                Invoke(f, {Nothing, EventArgs.Empty})
            Application.DoEvents()
            Assert.IsTrue(f.CheckedIn)
            f.Close()
        End Using

        Assert.AreEqual(2, Attendance.CheckInCountToday(_userId), "the second click has to land in the table")
        Assert.IsTrue(Attendance.LastCheckIn(_userId).Value > first,
                      "and the latest check-in must be the one just made, not the earlier one")
    End Sub

    <TestMethod>
    Public Sub The_screen_confirms_the_time_just_recorded_not_an_earlier_one()
        Attendance.CheckIn(_userId, "Confirm Clerk")
        Threading.Thread.Sleep(1100)

        Using f As New frmClerkWelcome(_userId, "Confirm Clerk")
            f.Show()
            Application.DoEvents()
            f.GetType().GetMethod("CheckIn_Click", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).
                Invoke(f, {Nothing, EventArgs.Empty})
            Application.DoEvents()

            Dim shown = DirectCast(FieldOf(f, "lblCheckState"), Control).Text
            Dim latest = Attendance.LastCheckIn(_userId).Value
            Assert.IsTrue(shown.Contains(latest.ToString("HH:mm")),
                          $"the screen says '{shown}' but she just checked in at {latest:HH:mm:ss}")
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Her_dashboard_card_shows_the_latest_check_in_not_the_first()
        Dim first = Attendance.CheckIn(_userId, "Dashboard Clerk")
        Threading.Thread.Sleep(1100)
        Dim latest = Attendance.CheckIn(_userId, "Dashboard Clerk")

        Using host As New Form()
            Dim uc As New ucDashboard(_userId, False)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()

            Dim text = String.Join(" ", Descendants(uc).Select(Function(c) c.Text))
            Assert.IsTrue(text.Contains(latest.ToString("HH:mm")),
                          $"the card should show the {latest:HH:mm} check-in she just made")
            If first.ToString("HH:mm") <> latest.ToString("HH:mm") Then
                Assert.IsFalse(text.Contains("Checked in " & first.ToString("HH:mm")),
                               "showing the first check-in makes the latest click look like it did nothing")
            End If
            host.Close()
        End Using
    End Sub

    Private Shared Function FieldOf(target As Object, name As String) As Object
        Return target.GetType().GetField(name, Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).GetValue(target)
    End Function

    Private Shared Function Descendants(c As Control) As List(Of Control)
        Dim all As New List(Of Control)
        For Each child As Control In c.Controls
            all.Add(child)
            all.AddRange(Descendants(child))
        Next
        Return all
    End Function

    ' ===== what the admin sees =====

    <TestMethod>
    Public Sub The_admin_trail_lists_every_check_in_separately()
        Attendance.CheckIn(_userId, "Trail Test") : Threading.Thread.Sleep(1100)
        Attendance.CheckIn(_userId, "Trail Test")

        Dim trail = Attendance.ForDateRange(Date.Today, Date.Today)
        Dim mine = trail.AsEnumerable().Where(Function(r) Convert.ToString(r("FullName")) = "Trail Test").ToList()

        Assert.AreEqual(2, mine.Count, "rolling the day up would hide the second check-in")
        Assert.IsTrue(mine.All(Function(r) Convert.ToString(r("Event")) = "Checked in"),
                      "and each line has to say what happened, in words")
    End Sub

    <TestMethod>
    Public Sub The_trail_reads_newest_first()
        Attendance.CheckIn(_userId, "Order Test") : Threading.Thread.Sleep(1100)
        Attendance.CheckOut(_userId)

        Dim trail = Attendance.ForDateRange(Date.Today, Date.Today)
        Assert.IsTrue(Convert.ToDateTime(trail.Rows(0)("At")) >= Convert.ToDateTime(trail.Rows(1)("At")),
                      "the most recent action belongs at the top")
    End Sub

    <TestMethod>
    Public Sub The_daily_summary_counts_the_check_ins_behind_it()
        Attendance.CheckIn(_userId, "Summary Test") : Threading.Thread.Sleep(1100)
        Attendance.CheckIn(_userId, "Summary Test") : Threading.Thread.Sleep(1100)
        Attendance.CheckOut(_userId)

        Dim summary = Attendance.DailySummary(Date.Today, Date.Today)
        Dim mine = summary.AsEnumerable().First(Function(r) Convert.ToString(r("FullName")) = "Summary Test")

        Assert.AreEqual(2, Convert.ToInt32(mine("Check-ins")), "the rolled-up line still has to admit there were two")
        Assert.IsTrue(Convert.ToDateTime(mine("Last out")) > Convert.ToDateTime(mine("First in")))
    End Sub

    <TestMethod>
    Public Sub Her_dashboard_shows_when_she_arrived_and_when_she_left()
        Attendance.CheckIn(_userId, "Dash Test") : Threading.Thread.Sleep(1100)
        Attendance.CheckOut(_userId)

        Dim row = Attendance.TodayRecord(_userId)
        Assert.IsNotNull(row)
        Assert.IsTrue(Convert.ToDateTime(row("CheckOutAt")) > Convert.ToDateTime(row("CheckInAt")))
    End Sub

    ' ===== the date can never drift from the time =====

    <TestMethod>
    Public Sub The_work_date_always_matches_the_moment_recorded()
        Attendance.CheckIn(_userId, "Date Test")

        Dim row = TestDb.Table(
            "SELECT WorkDate, HappenedAt FROM AttendanceEvents WHERE UserID = @u ORDER BY EventID DESC",
            TestDb.P("@u", _userId)).Rows(0)

        Assert.AreEqual(Convert.ToDateTime(row("HappenedAt")).Date, Convert.ToDateTime(row("WorkDate")).Date,
                        "WorkDate is computed from HappenedAt, so a late-night check-in lands on the right day")
    End Sub

    <TestMethod>
    Public Sub Nothing_is_recorded_for_a_day_with_no_activity()
        Assert.IsFalse(Attendance.TodayCheckIn(_userId).HasValue)
        Assert.AreEqual(0, Attendance.CheckInCountToday(_userId))
        Assert.IsNull(Attendance.TodayRecord(_userId))
    End Sub

End Class
