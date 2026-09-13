Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Clerk attendance: check-in from the welcome-clip card, check-out on signing
''' out, and the admin's record of both — one row per user per day.
<TestClass>
Public Class AttendanceTests

    Private _userId As Integer

    <TestInitialize>
    Public Sub Setup()
        _userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
        ' A clean slate for this user today, whatever earlier tests left behind.
        TestDb.Exec("DELETE FROM Attendance WHERE UserID=@u AND WorkDate=@d", TestDb.P("@u", _userId, "@d", Date.Today))
    End Sub

    <TestCleanup>
    Public Sub Cleanup()
        TestDb.Exec("DELETE FROM Attendance WHERE UserID=@u AND WorkDate=@d", TestDb.P("@u", _userId, "@d", Date.Today))
    End Sub

    <TestMethod>
    Public Sub Checking_in_logs_todays_time()
        Assert.IsFalse(Attendance.TodayCheckIn(_userId).HasValue, "no check-in yet")
        Dim before = DateTime.Now
        Dim at = Attendance.CheckIn(_userId, "Test Clerk")
        Assert.IsTrue(at >= before.AddSeconds(-2) AndAlso at <= DateTime.Now.AddSeconds(2))
        Assert.AreEqual(at, Attendance.TodayCheckIn(_userId))
    End Sub

    <TestMethod>
    Public Sub Checking_in_twice_the_same_day_keeps_the_first_time()
        Dim first = Attendance.CheckIn(_userId, "Test Clerk")
        Threading.Thread.Sleep(1100)   ' make sure a second call would get a different clock reading
        Dim second = Attendance.CheckIn(_userId, "Test Clerk")
        Assert.AreEqual(first, second, "a second check-in the same day must not overwrite the first")
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Attendance WHERE UserID=@u AND WorkDate=@d", TestDb.P("@u", _userId, "@d", Date.Today)))
    End Sub

    <TestMethod>
    Public Sub Checking_out_only_applies_after_a_check_in()
        Attendance.CheckOut(_userId)   ' no check-in today — must write nothing
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Attendance WHERE UserID=@u AND WorkDate=@d", TestDb.P("@u", _userId, "@d", Date.Today)))

        Attendance.CheckIn(_userId, "Test Clerk")
        Attendance.CheckOut(_userId)
        Dim rec = Attendance.TodayRecord(_userId)
        Assert.IsNotNull(rec)
        Assert.IsFalse(rec("CheckOutAt") Is DBNull.Value, "check-out must be recorded once she has checked in")
    End Sub

    <TestMethod>
    Public Sub Checking_out_again_moves_the_time_forward_to_the_latest_sign_out()
        Attendance.CheckIn(_userId, "Test Clerk")
        Attendance.CheckOut(_userId)
        Dim firstOut = Convert.ToDateTime(Attendance.TodayRecord(_userId)("CheckOutAt"))
        Threading.Thread.Sleep(1100)
        Attendance.CheckOut(_userId)
        Dim secondOut = Convert.ToDateTime(Attendance.TodayRecord(_userId)("CheckOutAt"))
        Assert.IsTrue(secondOut > firstOut, "the day's row should end with the LAST sign-out, not the first")
    End Sub

    <TestMethod>
    Public Sub Declining_to_check_in_is_still_on_record_with_no_check_in_time()
        Attendance.DeclineAndSignOut(_userId, "Test Clerk")
        Dim rec = Attendance.TodayRecord(_userId)
        Assert.IsNotNull(rec, "admin must be able to see that she signed in and back out")
        Assert.IsTrue(rec("CheckInAt") Is DBNull.Value, "she never actually checked in")
        Assert.IsFalse(rec("CheckOutAt") Is DBNull.Value)
    End Sub

    <TestMethod>
    Public Sub Admin_can_see_todays_record_in_the_date_range()
        Attendance.CheckIn(_userId, "Test Clerk")
        Attendance.CheckOut(_userId)
        Dim rows = Attendance.ForDateRange(Date.Today, Date.Today)
        Dim mine = rows.AsEnumerable().Where(Function(r) Convert.ToDateTime(r("WorkDate")).Date = Date.Today AndAlso
                                                          Convert.ToString(r("FullName")) = "Test Clerk").ToList()
        Assert.AreEqual(1, mine.Count)
        Assert.IsFalse(mine(0)("CheckInAt") Is DBNull.Value)
        Assert.IsFalse(mine(0)("CheckOutAt") Is DBNull.Value)
    End Sub

    <TestMethod>
    Public Sub A_date_outside_the_range_is_not_returned()
        Attendance.CheckIn(_userId, "Test Clerk")
        Dim rows = Attendance.ForDateRange(Date.Today.AddDays(-10), Date.Today.AddDays(-1))
        Dim mine = rows.AsEnumerable().Where(Function(r) Convert.ToString(r("FullName")) = "Test Clerk").ToList()
        Assert.AreEqual(0, mine.Count, "yesterday's range must not include today's check-in")
    End Sub

End Class
