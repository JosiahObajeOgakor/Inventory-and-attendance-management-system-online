Imports System.Data
Imports System.Data.SqlClient

''' Monthly payroll. Marking one employee's salary paid — whether from the
''' "Mark this row paid" button or as part of "Mark month paid" — is one
''' transaction: it flags that employee's month Paid, applies their loan
''' instalment, and posts ONE Expense line named for that employee and month.
''' Because each payment gets its own Expense row (not one lump sum for
''' everyone), Finance ▸ Expenses becomes a per-staff, per-month paid record —
''' exactly what "can be tracked" means here. Marking an already-paid row again
''' is a no-op, so a double-click never pays someone twice.
Public Module Payroll

    Public Class PaidResult
        Public Property AlreadyPaid As Boolean
        Public Property FullName As String
        Public Property Period As String
        Public Property NetPay As Decimal
    End Class

    ''' Marks one EmployeeMonthly row paid. Safe to call again on an
    ''' already-paid row (or to call twice at once, e.g. a doubled click) — the
    ''' UPDATE only ever takes hold once (guarded by "WHERE Paid = 0" inside the
    ''' transaction), so it can never post the expense or the loan instalment twice.
    Public Function MarkPaid(empMonthId As Integer, userId As Integer) As PaidResult
        Return DataAccess.InTransaction(
            Function(conn As SqlConnection, tx As SqlTransaction) As PaidResult
                Dim row = DataAccess.TableIn(conn, tx,
                    "SELECT em.EmployeeID, e.FullName, em.PeriodYear, em.PeriodMonth, em.SalaryAmount, em.LoanDeduction, em.Paid " &
                    "FROM EmployeeMonthly em JOIN Employees e ON e.EmployeeID = em.EmployeeID WHERE em.EmpMonthID = @id",
                    New Dictionary(Of String, Object) From {{"@id", empMonthId}})
                If row.Rows.Count = 0 Then Throw New InvalidOperationException("That payroll row no longer exists.")
                Dim r = row.Rows(0)
                Dim period = AppInfo.PeriodLabel(Convert.ToInt32(r("PeriodYear")), Convert.ToInt32(r("PeriodMonth")))
                Dim net = Convert.ToDecimal(r("SalaryAmount")) - Convert.ToDecimal(r("LoanDeduction"))
                Dim fullName = Convert.ToString(r("FullName"))
                Dim result As New PaidResult With {.FullName = fullName, .Period = period, .NetPay = net}

                If Convert.ToBoolean(r("Paid")) Then
                    result.AlreadyPaid = True
                    Return result
                End If

                Dim affected = DataAccess.Exec(conn, tx,
                    "UPDATE EmployeeMonthly SET Paid = 1, PaidDate = CAST(SYSDATETIME() AS DATE) WHERE EmpMonthID = @id AND Paid = 0",
                    New Dictionary(Of String, Object) From {{"@id", empMonthId}})
                If affected = 0 Then
                    result.AlreadyPaid = True   ' someone else's click won the race
                    Return result
                End If

                Dim loanCut = Convert.ToDecimal(r("LoanDeduction"))
                If loanCut > 0 Then ApplyLoanRepayment(conn, tx, Convert.ToInt32(r("EmployeeID")), loanCut, $"Payroll deduction {period}")

                DataAccess.Exec(conn, tx,
                    "INSERT INTO Expenses (Category, ExpenseDate, Amount, Note, CreatedByUserID) VALUES ('Salaries', CAST(SYSDATETIME() AS DATE), @a, @n, @u)",
                    New Dictionary(Of String, Object) From {{"@a", net}, {"@n", $"{fullName} — {period}"}, {"@u", userId}})

                result.AlreadyPaid = False
                Return result
            End Function)
    End Function

    ''' Marks every still-unpaid row for a month paid, one at a time (so each
    ''' still gets its own Expense line). Returns how many were actually paid.
    Public Function MarkAllPaid(year As Integer, month As Integer, userId As Integer) As Integer
        Dim ids = DataAccess.GetTable(
            "SELECT EmpMonthID FROM EmployeeMonthly WHERE PeriodYear = @y AND PeriodMonth = @m AND Paid = 0",
            New Dictionary(Of String, Object) From {{"@y", year}, {"@m", month}})
        Dim count = 0
        For Each r As DataRow In ids.Rows
            MarkPaid(Convert.ToInt32(r("EmpMonthID")), userId)
            count += 1
        Next
        Return count
    End Function

    ''' Every month this employee has been on payroll, newest first — the
    ''' record admin checks to see who's been paid and when.
    Public Function HistoryForEmployee(employeeId As Integer) As DataTable
        Return DataAccess.GetTable(
            "SELECT PeriodYear AS Year, PeriodMonth AS Month, Position, SalaryAmount, LoanDeduction, " &
            "(SalaryAmount - LoanDeduction) AS NetPay, Paid, PaidDate " &
            "FROM EmployeeMonthly WHERE EmployeeID = @e ORDER BY PeriodYear DESC, PeriodMonth DESC",
            New Dictionary(Of String, Object) From {{"@e", employeeId}})
    End Function

    ''' Reduces the employee's oldest open loan(s) by `amount`, inside the
    ''' caller's transaction, logging a repayment against each.
    Private Sub ApplyLoanRepayment(conn As SqlConnection, tx As SqlTransaction, employeeId As Integer, amount As Decimal, note As String)
        Dim remaining = amount
        Dim table = DataAccess.TableIn(conn, tx,
            "SELECT LoanID, Balance FROM EmployeeLoans WHERE EmployeeID = @e AND Closed = 0 ORDER BY LoanDate",
            New Dictionary(Of String, Object) From {{"@e", employeeId}})
        For Each l As DataRow In table.Rows
            If remaining <= 0 Then Exit For
            Dim loanId = Convert.ToInt32(l("LoanID"))
            Dim bal = Convert.ToDecimal(l("Balance"))
            Dim pay = Math.Min(bal, remaining)
            DataAccess.Exec(conn, tx, "INSERT INTO LoanRepayments (LoanID, Amount, Note) VALUES (@l, @a, @n)",
                New Dictionary(Of String, Object) From {{"@l", loanId}, {"@a", pay}, {"@n", note}})
            DataAccess.Exec(conn, tx,
                "UPDATE EmployeeLoans SET Balance = Balance - @a, Closed = CASE WHEN Balance - @a <= 0 THEN 1 ELSE 0 END WHERE LoanID = @l",
                New Dictionary(Of String, Object) From {{"@a", pay}, {"@l", loanId}})
            remaining -= pay
        Next
    End Sub

End Module
