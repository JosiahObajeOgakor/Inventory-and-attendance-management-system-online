Imports System.Data
Imports System.Windows.Forms
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Monthly payroll: marking one employee paid is a self-contained, trackable
''' record — its own Expense line, its own loan instalment, never paid twice —
''' and "mark the whole month" is just that, applied to everyone still unpaid.
<TestClass>
Public Class PayrollTests

    Private Const NonPublicInstance As Reflection.BindingFlags = Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance

    Private Shared Function Field(target As Object, name As String) As Object
        Return target.GetType().GetField(name, NonPublicInstance).GetValue(target)
    End Function

    Private Shared Sub Invoke(target As Object, method As String, ParamArray args As Object())
        target.GetType().GetMethod(method, NonPublicInstance).Invoke(target, args)
    End Sub

    Private _userId As Integer
    Private _employeeId As Integer
    Private _empMonthId As Integer
    Private ReadOnly _year As Integer = 2024   ' a year no other test touches
    Private ReadOnly _month As Integer = 6

    <TestInitialize>
    Public Sub Setup()
        _userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
        _employeeId = TestDb.AddEmployee("Payroll Test " & Guid.NewGuid().ToString("N").Substring(0, 6))
        _empMonthId = DataAccess.ExecuteScalarInsert(
            "INSERT INTO EmployeeMonthly (EmployeeID, PeriodYear, PeriodMonth, Position, SalaryAmount, LoanDeduction) " &
            "VALUES (@e, @y, @m, 'Clerk', 100000, 15000)",
            TestDb.P("@e", _employeeId, "@y", _year, "@m", _month))
    End Sub

    <TestMethod>
    Public Sub Marking_one_employee_paid_sets_the_paid_flag_and_date()
        Dim result = Payroll.MarkPaid(_empMonthId, _userId)
        Assert.IsFalse(result.AlreadyPaid)
        Assert.AreEqual(85000D, result.NetPay)

        Dim row = TestDb.Table("SELECT Paid, PaidDate FROM EmployeeMonthly WHERE EmpMonthID=@id", TestDb.P("@id", _empMonthId)).Rows(0)
        Assert.IsTrue(Convert.ToBoolean(row("Paid")))
        Assert.AreEqual(Date.Today, Convert.ToDateTime(row("PaidDate")).Date)
    End Sub

    <TestMethod>
    Public Sub Marking_one_employee_paid_posts_a_named_expense_line()
        Dim before = TestDb.Count("SELECT COUNT(*) FROM Expenses WHERE Category='Salaries'")
        Dim result = Payroll.MarkPaid(_empMonthId, _userId)

        Assert.AreEqual(before + 1, TestDb.Count("SELECT COUNT(*) FROM Expenses WHERE Category='Salaries'"),
                        "each payment must post its own expense line, not a lump sum")
        Dim note = Convert.ToString(TestDb.Scalar("SELECT TOP 1 Note FROM Expenses WHERE Category='Salaries' ORDER BY ExpenseID DESC"))
        Assert.IsTrue(note.Contains(result.FullName), "the expense must name who was paid: " & note)
        Assert.IsTrue(note.Contains(result.Period), "the expense must name which month: " & note)
        Dim amount = Convert.ToDecimal(TestDb.Scalar("SELECT TOP 1 Amount FROM Expenses WHERE Category='Salaries' ORDER BY ExpenseID DESC"))
        Assert.AreEqual(85000D, amount)
    End Sub

    <TestMethod>
    Public Sub Marking_one_employee_paid_applies_the_loan_instalment()
        Dim loanId = DataAccess.ExecuteScalarInsert(
            "INSERT INTO EmployeeLoans (EmployeeID, Principal, Balance) VALUES (@e, 50000, 50000)", TestDb.P("@e", _employeeId))
        Payroll.MarkPaid(_empMonthId, _userId)

        Dim balance = Convert.ToDecimal(TestDb.Scalar("SELECT Balance FROM EmployeeLoans WHERE LoanID=@l", TestDb.P("@l", loanId)))
        Assert.AreEqual(35000D, balance, "the 15,000 loan deduction must come off the balance")
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM LoanRepayments WHERE LoanID=@l", TestDb.P("@l", loanId)))
    End Sub

    <TestMethod>
    Public Sub Marking_an_already_paid_row_again_does_nothing_twice()
        Payroll.MarkPaid(_empMonthId, _userId)
        Dim expensesAfterFirst = TestDb.Count("SELECT COUNT(*) FROM Expenses WHERE Category='Salaries'")

        Dim second = Payroll.MarkPaid(_empMonthId, _userId)

        Assert.IsTrue(second.AlreadyPaid)
        Assert.AreEqual(expensesAfterFirst, TestDb.Count("SELECT COUNT(*) FROM Expenses WHERE Category='Salaries'"),
                        "a second mark-paid must never post a second expense — nobody gets paid twice")
    End Sub

    <TestMethod>
    Public Sub Mark_all_paid_only_touches_still_unpaid_rows_and_pays_each_separately()
        Dim other = TestDb.AddEmployee("Payroll Test 2 " & Guid.NewGuid().ToString("N").Substring(0, 6))
        Dim otherMonthId = DataAccess.ExecuteScalarInsert(
            "INSERT INTO EmployeeMonthly (EmployeeID, PeriodYear, PeriodMonth, Position, SalaryAmount) VALUES (@e, @y, @m, 'Clerk', 60000)",
            TestDb.P("@e", other, "@y", _year, "@m", _month))
        ' One of the two is already paid — must not be paid (or expensed) again.
        Payroll.MarkPaid(_empMonthId, _userId)
        Dim expensesBefore = TestDb.Count("SELECT COUNT(*) FROM Expenses WHERE Category='Salaries'")

        Dim paidCount = Payroll.MarkAllPaid(_year, _month, _userId)

        Assert.AreEqual(1, paidCount, "only the still-unpaid employee should be paid by this run")
        Assert.AreEqual(expensesBefore + 1, TestDb.Count("SELECT COUNT(*) FROM Expenses WHERE Category='Salaries'"))
        Assert.IsTrue(Convert.ToBoolean(TestDb.Scalar("SELECT Paid FROM EmployeeMonthly WHERE EmpMonthID=@id", TestDb.P("@id", otherMonthId))))
    End Sub

    ' ===== where the salary actually comes from =====

    <TestMethod>
    Public Sub Add_employee_dialog_has_a_monthly_salary_field()
        Using f As New frmAddEmployee()
            f.Show()
            Application.DoEvents()
            Assert.IsNotNull(Field(f, "numSalary"), "there must be somewhere on this screen to set what they're paid")
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Saving_a_new_employee_through_the_dialog_records_the_salary_entered()
        Using f As New frmAddEmployee()
            f.Show()
            Application.DoEvents()
            DirectCast(Field(f, "txtName"), TextBox).Text = "Dialog Salary Test " & Guid.NewGuid().ToString("N").Substring(0, 6)
            DirectCast(Field(f, "txtPosition"), TextBox).Text = "Clerk"
            DirectCast(Field(f, "numSalary"), NumericUpDown).Value = 95000D
            Invoke(f, "Save_Click", Nothing, EventArgs.Empty)
        End Using
        Dim salary = Convert.ToDecimal(TestDb.Scalar("SELECT TOP 1 MonthlySalary FROM Employees WHERE FullName LIKE 'Dialog Salary Test%' ORDER BY EmployeeID DESC"))
        Assert.AreEqual(95000D, salary)
    End Sub

    <TestMethod>
    Public Sub Adding_an_employee_stores_their_monthly_salary()
        Dim id = TestDb.AddEmployee("Salary Field Test " & Guid.NewGuid().ToString("N").Substring(0, 6), monthlySalary:=120000D)
        Assert.AreEqual(120000D, Convert.ToDecimal(TestDb.Scalar("SELECT MonthlySalary FROM Employees WHERE EmployeeID=@e", TestDb.P("@e", id))))
    End Sub

    <TestMethod>
    Public Sub A_brand_new_employees_first_generated_month_uses_their_set_salary_not_zero()
        Dim id = TestDb.AddEmployee("New Hire " & Guid.NewGuid().ToString("N").Substring(0, 6), monthlySalary:=85000D)

        Using host As New Form()
            Dim uc As New ucEmployees(_userId)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            DirectCast(Field(uc, "cboYear"), ComboBox).SelectedItem = _year
            DirectCast(Field(uc, "cboMonth"), ComboBox).SelectedIndex = _month - 1
            Invoke(uc, "Generate_Click", Nothing, EventArgs.Empty)
            host.Close()
        End Using

        Dim salary = Convert.ToDecimal(TestDb.Scalar(
            "SELECT SalaryAmount FROM EmployeeMonthly WHERE EmployeeID=@e AND PeriodYear=@y AND PeriodMonth=@m",
            TestDb.P("@e", id, "@y", _year, "@m", _month)))
        Assert.AreEqual(85000D, salary, "a new employee's first payroll row must start from their set salary, not 0")
    End Sub

    <TestMethod>
    Public Sub A_later_month_still_carries_forward_what_was_actually_paid_last()
        ' Someone already has a paid month at a different amount than their
        ' current "Monthly salary" (e.g. a raise not yet reflected on file, or a
        ' one-off adjustment) — the next generated month must follow the actual
        ' history, not silently reset to the Employees.MonthlySalary default.
        Dim id = TestDb.AddEmployee("Raise Test " & Guid.NewGuid().ToString("N").Substring(0, 6), monthlySalary:=50000D)
        DataAccess.Execute(
            "INSERT INTO EmployeeMonthly (EmployeeID, PeriodYear, PeriodMonth, Position, SalaryAmount) VALUES (@e, @y, @m, 'Clerk', 70000)",
            TestDb.P("@e", id, "@y", _year, "@m", _month - 1))

        Using host As New Form()
            Dim uc As New ucEmployees(_userId)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            DirectCast(Field(uc, "cboYear"), ComboBox).SelectedItem = _year
            DirectCast(Field(uc, "cboMonth"), ComboBox).SelectedIndex = _month - 1
            Invoke(uc, "Generate_Click", Nothing, EventArgs.Empty)
            host.Close()
        End Using

        Dim salary = Convert.ToDecimal(TestDb.Scalar(
            "SELECT SalaryAmount FROM EmployeeMonthly WHERE EmployeeID=@e AND PeriodYear=@y AND PeriodMonth=@m",
            TestDb.P("@e", id, "@y", _year, "@m", _month)))
        Assert.AreEqual(70000D, salary, "must carry forward the last actual payroll amount, not the Employees default")
    End Sub

    <TestMethod>
    Public Sub Staff_list_shows_each_employees_monthly_salary()
        Dim name = "Staff Grid Test " & Guid.NewGuid().ToString("N").Substring(0, 6)
        TestDb.AddEmployee(name, monthlySalary:=64000D)
        Using host As New Form()
            Dim uc As New ucEmployees(_userId)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            ' AllRows, not the grid's DataSource: that's only the page on screen,
            ' and a new employee needn't land on the first page of five. Matched on
            ' name because AllRows drops the grid's hidden EmployeeID column.
            Dim data = DirectCast(Field(uc, "gridStaff"), PagedGrid).AllRows()
            Dim row = data.AsEnumerable().First(Function(r) CStr(r("FullName")) = name)
            Assert.AreEqual(64000D, Convert.ToDecimal(row("MonthlySalary")))
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Payroll_history_lists_every_month_for_an_employee_newest_first()
        DataAccess.ExecuteScalarInsert(
            "INSERT INTO EmployeeMonthly (EmployeeID, PeriodYear, PeriodMonth, Position, SalaryAmount) VALUES (@e, @y, @m, 'Clerk', 90000)",
            TestDb.P("@e", _employeeId, "@y", _year, "@m", _month + 1))
        Payroll.MarkPaid(_empMonthId, _userId)

        Dim history = Payroll.HistoryForEmployee(_employeeId)
        Assert.AreEqual(2, history.Rows.Count)
        Assert.AreEqual(_month + 1, Convert.ToInt32(history.Rows(0)("Month")), "newest month must come first")
        Assert.IsFalse(Convert.ToBoolean(history.Rows(0)("Paid")), "the newer month was never marked paid")
        Assert.IsTrue(Convert.ToBoolean(history.Rows(1)("Paid")), "the earlier month was paid in this test")
    End Sub

End Class
