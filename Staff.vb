Imports System.Data

''' Employee records: what a deletion would take with it, and the deletions
''' themselves. Salary expenses already posted to Finance are never touched.
Public Module Staff

    ''' How many payroll months and loans hang off this employee.
    Public Function RecordCounts(employeeId As Integer) As (Months As Integer, Loans As Integer)
        Dim r = DataAccess.GetTable(
            "SELECT (SELECT COUNT(*) FROM EmployeeMonthly WHERE EmployeeID = @e) AS Months, " &
            "(SELECT COUNT(*) FROM EmployeeLoans WHERE EmployeeID = @e) AS Loans",
            New Dictionary(Of String, Object) From {{"@e", employeeId}}).Rows(0)
        Return (Convert.ToInt32(r("Months")), Convert.ToInt32(r("Loans")))
    End Function

    ''' Deletes an employee with their payroll months, loans and repayments.
    Public Sub DeleteEmployee(employeeId As Integer)
        Dim p = Function() New Dictionary(Of String, Object) From {{"@e", employeeId}}
        DataAccess.ExecuteTransaction(New List(Of (Sql As String, Params As Dictionary(Of String, Object))) From {
            ("DELETE FROM LoanRepayments WHERE LoanID IN (SELECT LoanID FROM EmployeeLoans WHERE EmployeeID = @e)", p()),
            ("DELETE FROM EmployeeLoans WHERE EmployeeID = @e", p()),
            ("DELETE FROM EmployeeMonthly WHERE EmployeeID = @e", p()),
            ("DELETE FROM Employees WHERE EmployeeID = @e", p())
        })
    End Sub

    ''' Deletes one loan and its repayment history.
    Public Sub DeleteLoan(loanId As Integer)
        Dim p = Function() New Dictionary(Of String, Object) From {{"@l", loanId}}
        DataAccess.ExecuteTransaction(New List(Of (Sql As String, Params As Dictionary(Of String, Object))) From {
            ("DELETE FROM LoanRepayments WHERE LoanID = @l", p()),
            ("DELETE FROM EmployeeLoans WHERE LoanID = @l", p())
        })
    End Sub

End Module
