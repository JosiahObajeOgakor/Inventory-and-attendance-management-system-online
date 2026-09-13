Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Clearing out test data: deleting employees (with their payroll and loans),
''' deleting a single loan, and deleting ledger lines.
<TestClass>
Public Class StaffAndLedgerTests

    <TestMethod>
    Public Sub Deleting_an_employee_takes_payroll_loans_and_repayments_with_it()
        Dim id = TestDb.AddEmployee("Test Person " & Guid.NewGuid().ToString("N").Substring(0, 5))
        TestDb.Exec("INSERT INTO EmployeeMonthly (EmployeeID, PeriodYear, PeriodMonth, Position, SalaryAmount) VALUES (@e, 2026, 4, 'Clerk', 50000)", TestDb.P("@e", id))
        TestDb.Exec("INSERT INTO EmployeeMonthly (EmployeeID, PeriodYear, PeriodMonth, Position, SalaryAmount) VALUES (@e, 2026, 5, 'Clerk', 50000)", TestDb.P("@e", id))
        Dim loan = DataAccess.ExecuteScalarInsert("INSERT INTO EmployeeLoans (EmployeeID, Principal, Balance) VALUES (@e, 20000, 15000)", TestDb.P("@e", id))
        TestDb.Exec("INSERT INTO LoanRepayments (LoanID, Amount) VALUES (@l, 5000)", TestDb.P("@l", loan))

        Dim counts = Staff.RecordCounts(id)
        Assert.AreEqual(2, counts.Months)
        Assert.AreEqual(1, counts.Loans)

        Staff.DeleteEmployee(id)

        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Employees WHERE EmployeeID=@e", TestDb.P("@e", id)))
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM EmployeeMonthly WHERE EmployeeID=@e", TestDb.P("@e", id)))
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM EmployeeLoans WHERE EmployeeID=@e", TestDb.P("@e", id)))
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM LoanRepayments WHERE LoanID=@l", TestDb.P("@l", loan)))
    End Sub

    <TestMethod>
    Public Sub Deleting_an_employee_keeps_salary_expenses_already_posted()
        Dim id = TestDb.AddEmployee("Paid Person " & Guid.NewGuid().ToString("N").Substring(0, 5))
        Dim note = "Payroll test " & Guid.NewGuid().ToString("N").Substring(0, 6)
        TestDb.Exec("INSERT INTO Expenses (Category, Amount, Note, CreatedByUserID) VALUES ('Salaries', 50000, @n, (SELECT MIN(UserID) FROM Users))", TestDb.P("@n", note))

        Staff.DeleteEmployee(id)

        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Expenses WHERE Note=@n", TestDb.P("@n", note)),
                        "money already paid out must stay in Finance")
    End Sub

    <TestMethod>
    Public Sub Deleting_one_loan_leaves_the_employee_and_other_loans()
        Dim id = TestDb.AddEmployee("Loan Person " & Guid.NewGuid().ToString("N").Substring(0, 5))
        Dim keep = DataAccess.ExecuteScalarInsert("INSERT INTO EmployeeLoans (EmployeeID, Principal, Balance) VALUES (@e, 10000, 10000)", TestDb.P("@e", id))
        Dim drop = DataAccess.ExecuteScalarInsert("INSERT INTO EmployeeLoans (EmployeeID, Principal, Balance) VALUES (@e, 30000, 30000)", TestDb.P("@e", id))
        TestDb.Exec("INSERT INTO LoanRepayments (LoanID, Amount) VALUES (@l, 1000)", TestDb.P("@l", drop))

        Staff.DeleteLoan(drop)

        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Employees WHERE EmployeeID=@e", TestDb.P("@e", id)))
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM EmployeeLoans WHERE EmployeeID=@e", TestDb.P("@e", id)))
        Assert.AreEqual(keep, TestDb.Count("SELECT LoanID FROM EmployeeLoans WHERE EmployeeID=@e", TestDb.P("@e", id)))
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM LoanRepayments WHERE LoanID=@l", TestDb.P("@l", drop)))
    End Sub

    <TestMethod>
    Public Sub Deleting_a_ledger_line_leaves_the_customer_balance_alone()
        Dim customer = TestDb.AddCustomer("Ledger Customer " & Guid.NewGuid().ToString("N").Substring(0, 5))
        TestDb.Exec("UPDATE Customers SET Balance = 25000 WHERE CustomerID=@c", TestDb.P("@c", customer))
        Dim reference = "REF-" & Guid.NewGuid().ToString("N").Substring(0, 8)
        TestDb.Exec("INSERT INTO Ledger (AccountType, AccountName, EntryType, Amount, Reference) VALUES ('Customer', 'Ledger Customer', 'Debit', 25000, @r)", TestDb.P("@r", reference))

        Dim id = TestDb.Count("SELECT LedgerID FROM Ledger WHERE Reference=@r", TestDb.P("@r", reference))
        TestDb.Exec("DELETE FROM Ledger WHERE LedgerID=@id", TestDb.P("@id", id))

        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Ledger WHERE Reference=@r", TestDb.P("@r", reference)))
        Assert.AreEqual(25000D, Convert.ToDecimal(TestDb.Scalar("SELECT Balance FROM Customers WHERE CustomerID=@c", TestDb.P("@c", customer))),
                        "the ledger is history only — balances are stored on the customer")
    End Sub

End Class
