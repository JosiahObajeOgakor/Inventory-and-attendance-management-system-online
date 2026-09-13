Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data

''' Employees. Three tabs:
'''   • Monthly payroll — pick year/month, "Generate" creates a row per active
'''     employee (name, position, salary carried from last month); edit salary
'''     or loan deduction inline. "Mark this row paid" pays one employee —
'''     the normal day-to-day action, since staff are rarely all paid in one
'''     go — and "Mark month paid" catches everyone still unpaid at once. Each
'''     payment posts its own Salaries expense line and applies that person's
'''     loan instalment (see Payroll.vb); "Payroll history" on the Staff tab
'''     shows every month a given employee has (or hasn't) been paid.
'''   • Staff & loans — add / edit employees; give a loan; record repayments
'''     bit by bit; see each loan's running balance.
'''   • Attendance — admin's view of clerk check-in/out (see Attendance.vb).
Public Class ucEmployees
    Inherits UserControl

    Private ReadOnly currentUserId As Integer

    ' payroll tab
    Private cboYear As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 90}
    Private cboMonth As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 130}
    Private btnGenerate As New Button() With {.Text = "Generate month", .Tag = "primary", .AutoSize = True}
    Private btnMarkOnePaid As New Button() With {.Text = "Mark this row paid", .Tag = "primary", .AutoSize = True, .Enabled = False}
    Private btnMarkPaid As New Button() With {.Text = "Mark month paid", .AutoSize = True}
    Private btnExportPay As New Button() With {.Text = "Export CSV", .AutoSize = True}
    Private ReadOnly gridPay As New PagedGrid() With {.PageSize = 5}
    Private lblPayTotal As New Label() With {.AutoSize = True, .Tag = "keepfont", .Font = New Font("Segoe UI", 11, FontStyle.Bold)}

    ' staff/loans tab
    Private ReadOnly gridStaff As New PagedGrid() With {.PageSize = 5}
    Private ReadOnly gridLoans As New PagedGrid() With {.PageSize = 5}
    Private btnAddStaff As New Button() With {.Text = "+ Add employee", .Tag = "primary", .AutoSize = True}
    Private btnEditStaff As New Button() With {.Text = "Edit", .AutoSize = True, .Enabled = False}
    Private btnToggle As New Button() With {.Text = "Activate / deactivate", .AutoSize = True, .Enabled = False}
    Private btnDeleteStaff As New Button() With {.Text = "Delete employee", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private btnGiveLoan As New Button() With {.Text = "Give loan", .AutoSize = True, .Enabled = False}
    Private btnRepay As New Button() With {.Text = "Record repayment", .AutoSize = True, .Enabled = False}
    Private btnDeleteLoan As New Button() With {.Text = "Delete loan", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private btnRemovePayRow As New Button() With {.Text = "Remove from month", .Tag = "danger", .AutoSize = True}
    Private btnPayrollHistory As New Button() With {.Text = "Payroll history", .AutoSize = True, .Enabled = False}
    Private txtSearchStaff As New TextBox() With {.Width = 180}

    ' attendance tab
    Private dtpAttFrom As New DateTimePicker() With {.Format = DateTimePickerFormat.Custom, .CustomFormat = "dd MMM yyyy", .Width = 130}
    Private dtpAttTo As New DateTimePicker() With {.Format = DateTimePickerFormat.Custom, .CustomFormat = "dd MMM yyyy", .Width = 130}
    Private btnAttRefresh As New Button() With {.Text = "Refresh", .Tag = "primary", .AutoSize = True}
    Private btnExportAtt As New Button() With {.Text = "Export CSV", .AutoSize = True}
    Private ReadOnly gridAttendance As New PagedGrid() With {.PageSize = 5}

    Public Sub New(userId As Integer)
        currentUserId = userId
        gridPay.Grid.ReadOnly = False

        Dim tabs As New TabControl() With {.Dock = DockStyle.Fill}
        tabs.TabPages.Add(BuildPayrollTab())
        tabs.TabPages.Add(BuildStaffTab())
        tabs.TabPages.Add(BuildAttendanceTab())
        Controls.Add(tabs)

        dtpAttFrom.Value = New Date(DateTime.Today.Year, DateTime.Today.Month, 1)
        dtpAttTo.Value = DateTime.Today

        Dim thisYear = DateTime.Now.Year
        For y = thisYear To thisYear - 5 Step -1
            cboYear.Items.Add(y)
        Next
        cboYear.SelectedItem = thisYear
        For m = 1 To 12
            cboMonth.Items.Add(AppInfo.MonthName(m))
        Next
        cboMonth.SelectedIndex = DateTime.Now.Month - 1

        AddHandler cboYear.SelectedIndexChanged, Sub(s, e) LoadPayroll()
        AddHandler cboMonth.SelectedIndexChanged, Sub(s, e) LoadPayroll()
        AddHandler btnGenerate.Click, AddressOf Generate_Click
        AddHandler btnMarkOnePaid.Click, AddressOf MarkOnePaid_Click
        AddHandler btnMarkPaid.Click, AddressOf MarkPaid_Click
        AddHandler btnExportPay.Click, Sub(s, e) AppUI.ExportCsv(gridPay.AllRows(), "payroll", FindForm())
        AddHandler gridPay.Grid.CellEndEdit, AddressOf PayCellEdited
        ' Salary and loan stay editable, everything else read-only — re-applied
        ' per page, since turning one rebuilds the columns.
        AddHandler gridPay.PageBound, Sub(s, e)
                                          For Each c As DataGridViewColumn In gridPay.Grid.Columns
                                              c.ReadOnly = (c.Name <> "SalaryAmount" AndAlso c.Name <> "LoanDeduction")
                                          Next
                                      End Sub
        AddHandler gridPay.Grid.SelectionChanged, Sub(s, e)
                                                 btnMarkOnePaid.Enabled = SelectedIsUnpaid()
                                             End Sub

        AddHandler gridStaff.Grid.SelectionChanged, Sub(s, e)
                                                   Dim has = gridStaff.Grid.SelectedRows.Count > 0
                                                   btnEditStaff.Enabled = has : btnToggle.Enabled = has
                                                   btnDeleteStaff.Enabled = has
                                                   btnGiveLoan.Enabled = has : btnRepay.Enabled = False
                                                   btnPayrollHistory.Enabled = has
                                                   LoadLoans()
                                               End Sub
        AddHandler gridLoans.Grid.SelectionChanged, Sub(s, e)
                                                   Dim has = gridLoans.Grid.SelectedRows.Count > 0
                                                   btnRepay.Enabled = has : btnDeleteLoan.Enabled = has
                                               End Sub
        AddHandler btnDeleteStaff.Click, AddressOf DeleteStaff_Click
        AddHandler btnDeleteLoan.Click, AddressOf DeleteLoan_Click
        AddHandler btnRemovePayRow.Click, AddressOf RemovePayRow_Click
        AddHandler btnAddStaff.Click, Sub(s, e) EditEmployee(Nothing)
        AddHandler btnEditStaff.Click, Sub(s, e) EditEmployee(SelectedEmployeeId())
        AddHandler btnToggle.Click, AddressOf Toggle_Click
        AddHandler btnGiveLoan.Click, AddressOf GiveLoan_Click
        AddHandler btnRepay.Click, AddressOf Repay_Click
        AddHandler btnPayrollHistory.Click, AddressOf PayrollHistory_Click
        AddHandler txtSearchStaff.TextChanged, Sub(s, e) LoadStaff()
        AddHandler btnAttRefresh.Click, Sub(s, e) LoadAttendance()
        AddHandler btnExportAtt.Click, Sub(s, e) AppUI.ExportCsv(gridAttendance.AllRows(), "attendance", FindForm())
        AddHandler dtpAttFrom.ValueChanged, Sub(s, e) LoadAttendance()
        AddHandler dtpAttTo.ValueChanged, Sub(s, e) LoadAttendance()

        AddHandler Me.Load, Sub(s, e)
                                LoadPayroll()
                                LoadStaff()
                                LoadAttendance()
                            End Sub
    End Sub

    ' ---- payroll tab ----
    Private Function BuildPayrollTab() As TabPage
        Dim tp As New TabPage("Monthly payroll")
        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(10)}
        bar.Controls.Add(cboYear)
        bar.Controls.Add(cboMonth)
        bar.Controls.Add(btnGenerate)
        bar.Controls.Add(btnMarkOnePaid)
        bar.Controls.Add(btnMarkPaid)
        bar.Controls.Add(btnExportPay)
        bar.Controls.Add(btnRemovePayRow)
        bar.Controls.Add(lblPayTotal)
        Dim host As New Panel() With {.Dock = DockStyle.Fill}
        host.Controls.Add(gridPay)
        host.Controls.Add(bar)
        tp.Controls.Add(host)
        Return tp
    End Function

    Private ReadOnly Property PYear As Integer
        Get
            Return CInt(cboYear.SelectedItem)
        End Get
    End Property
    Private ReadOnly Property PMonth As Integer
        Get
            Return cboMonth.SelectedIndex + 1
        End Get
    End Property

    Private Sub LoadPayroll()
        Dim t = DataAccess.GetTable(
            "SELECT em.EmpMonthID, e.FullName, em.Position, em.SalaryAmount, em.LoanDeduction, " &
            "(em.SalaryAmount - em.LoanDeduction) AS NetPay, em.Paid, em.PaidDate " &
            "FROM EmployeeMonthly em JOIN Employees e ON e.EmployeeID=em.EmployeeID " &
            "WHERE em.PeriodYear=@y AND em.PeriodMonth=@m ORDER BY e.FullName",
            New Dictionary(Of String, Object) From {{"@y", PYear}, {"@m", PMonth}})
        gridPay.Bind(t, hiddenColumns:={"EmpMonthID"})
        Dim total = t.AsEnumerable().Sum(Function(r) Convert.ToDecimal(r("NetPay")))
        Dim paid = t.AsEnumerable().Where(Function(r) Convert.ToBoolean(r("Paid"))).Sum(Function(r) Convert.ToDecimal(r("NetPay")))
        lblPayTotal.Text = $"Net payroll {AppInfo.PeriodLabel(PYear, PMonth)}: {AppInfo.Money(total)}   ·   paid {AppInfo.Money(paid)}"
        btnMarkPaid.Enabled = t.Rows.Count > 0
    End Sub

    Private Sub PayCellEdited(sender As Object, e As DataGridViewCellEventArgs)
        If e.RowIndex < 0 Then Return
        Dim row = gridPay.Grid.Rows(e.RowIndex)
        Dim id = CInt(row.Cells("EmpMonthID").Value)
        Dim salary = ToDec(row.Cells("SalaryAmount").Value)
        Dim loan = ToDec(row.Cells("LoanDeduction").Value)
        DataAccess.Execute("UPDATE EmployeeMonthly SET SalaryAmount=@s, LoanDeduction=@l WHERE EmpMonthID=@id",
            New Dictionary(Of String, Object) From {{"@s", salary}, {"@l", loan}, {"@id", id}})
        LoadPayroll()
    End Sub

    Private Shared Function ToDec(v As Object) As Decimal
        Dim d As Decimal
        Return If(v IsNot Nothing AndAlso Decimal.TryParse(Convert.ToString(v), d), d, 0D)
    End Function

    Private Sub Generate_Click(sender As Object, e As EventArgs)
        ' Carry each active employee forward: salary from their most recent paid
        ' month if they have one, otherwise the "Monthly salary" set on their
        ' record (Staff & loans ▸ Add/Edit employee) — never defaults to 0 for
        ' someone whose salary was actually set up. Loan instalment suggested
        ' from any open loan (spread over 6 months, capped at the balance).
        Dim emps = DataAccess.GetTable("SELECT EmployeeID, FullName, Position, MonthlySalary FROM Employees WHERE IsActive=1")
        Dim added = 0
        For Each em As DataRow In emps.Rows
            Dim eid = CInt(em("EmployeeID"))
            If DataAccess.GetTable("SELECT 1 FROM EmployeeMonthly WHERE EmployeeID=@e AND PeriodYear=@y AND PeriodMonth=@m",
                New Dictionary(Of String, Object) From {{"@e", eid}, {"@y", PYear}, {"@m", PMonth}}).Rows.Count > 0 Then Continue For

            Dim lastSalary = DataAccess.GetTable(
                "SELECT TOP 1 SalaryAmount FROM EmployeeMonthly WHERE EmployeeID=@e ORDER BY PeriodYear DESC, PeriodMonth DESC",
                New Dictionary(Of String, Object) From {{"@e", eid}})
            Dim salary As Decimal = If(lastSalary.Rows.Count > 0, Convert.ToDecimal(lastSalary.Rows(0)(0)), Convert.ToDecimal(em("MonthlySalary")))

            Dim loanBal = Convert.ToDecimal(DataAccess.GetTable(
                "SELECT ISNULL(SUM(Balance),0) FROM EmployeeLoans WHERE EmployeeID=@e AND Closed=0",
                New Dictionary(Of String, Object) From {{"@e", eid}}).Rows(0)(0))
            Dim instalment = Math.Min(loanBal, Math.Round(loanBal / 6D, 2))

            DataAccess.Execute(
                "INSERT INTO EmployeeMonthly (EmployeeID, PeriodYear, PeriodMonth, Position, SalaryAmount, LoanDeduction) " &
                "VALUES (@e, @y, @m, @pos, @s, @l)",
                New Dictionary(Of String, Object) From {
                    {"@e", eid}, {"@y", PYear}, {"@m", PMonth}, {"@pos", Convert.ToString(em("Position"))},
                    {"@s", salary}, {"@l", instalment}})
            added += 1
        Next
        AppUI.Toast(If(added > 0, $"Added {added} employee(s) to {AppInfo.PeriodLabel(PYear, PMonth)}.", "Everyone is already on this month."),
                    If(added > 0, AppUI.ToastKind.Success, AppUI.ToastKind.Info))
        LoadPayroll()
    End Sub

    ''' Whether the selected payroll row is still unpaid (so "Mark this row
    ''' paid" only ever offers to do something).
    Private Function SelectedIsUnpaid() As Boolean
        If gridPay.Grid.SelectedRows.Count = 0 Then Return False
        Return Not Convert.ToBoolean(gridPay.Grid.SelectedRows(0).Cells("Paid").Value)
    End Function

    ''' Pays just the selected employee for this month — the day-to-day action,
    ''' since staff are rarely all paid in one go. Each payment posts its own
    ''' Salaries expense line named for that employee and month, so Finance ▸
    ''' Expenses becomes the tracked record of who was paid, and when.
    Private Sub MarkOnePaid_Click(sender As Object, e As EventArgs)
        If gridPay.Grid.SelectedRows.Count = 0 Then Return
        Dim empMonthId = CInt(gridPay.Grid.SelectedRows(0).Cells("EmpMonthID").Value)
        Try
            Dim result = Payroll.MarkPaid(empMonthId, currentUserId)
            If result.AlreadyPaid Then
                AppUI.Toast($"{result.FullName} was already marked paid for {result.Period}.", AppUI.ToastKind.Info)
            Else
                AppUI.Toast($"{result.FullName} marked paid for {result.Period} — {AppInfo.Money(result.NetPay)}.", AppUI.ToastKind.Success)
            End If
            LoadPayroll()
            LoadStaff()
        Catch ex As Exception
            AppUI.Toast("Could not mark paid: " & ex.Message, AppUI.ToastKind.Error)
        End Try
    End Sub

    ''' Pays everyone still unpaid for the chosen month, one at a time.
    Private Sub MarkPaid_Click(sender As Object, e As EventArgs)
        Dim unpaidCount = DataAccess.GetTable(
            "SELECT COUNT(*) FROM EmployeeMonthly WHERE PeriodYear=@y AND PeriodMonth=@m AND Paid=0",
            New Dictionary(Of String, Object) From {{"@y", PYear}, {"@m", PMonth}}).Rows(0)(0)
        If Convert.ToInt32(unpaidCount) = 0 Then
            AppUI.Info(FindForm(), "Nothing unpaid for this month.")
            Return
        End If
        If Not AppUI.Confirm(FindForm(),
            $"Mark all {unpaidCount} still-unpaid employee(s) paid for {AppInfo.PeriodLabel(PYear, PMonth)}?" & vbCrLf & vbCrLf &
            "Each posts its own Salaries expense and applies their loan instalment.",
            "Confirm payroll", "Pay now") Then Return

        Try
            Dim paidCount = Payroll.MarkAllPaid(PYear, PMonth, currentUserId)
            AppUI.Toast($"{paidCount} employee(s) paid for {AppInfo.PeriodLabel(PYear, PMonth)}.", AppUI.ToastKind.Success)
        Catch ex As Exception
            AppUI.Toast("Payroll run failed part-way — check the list below for who's still unpaid: " & ex.Message, AppUI.ToastKind.Error)
        End Try
        LoadPayroll()
        LoadStaff()
    End Sub

    ''' Every month this employee has been on payroll — whether each was paid,
    ''' and when — so a payment is never just "somewhere in the grid" but a
    ''' record admin can pull up per person, at any time.
    Private Sub PayrollHistory_Click(sender As Object, e As EventArgs)
        Dim eid = SelectedEmployeeId()
        If eid Is Nothing Then Return
        Dim name = Convert.ToString(gridStaff.Grid.SelectedRows(0).Cells("FullName").Value)
        Using f As New frmPayrollHistory(eid.Value, name)
            f.ShowDialog(FindForm())
        End Using
    End Sub

    ' ---- staff & loans tab ----
    Private Function BuildStaffTab() As TabPage
        Dim tp As New TabPage("Staff & loans")
        Dim split As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .RowCount = 2}
        split.RowStyles.Add(New RowStyle(SizeType.Percent, 50))
        split.RowStyles.Add(New RowStyle(SizeType.Percent, 50))

        Dim topBar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8)}
        topBar.Controls.Add(New Label() With {.Text = "Employees", .Tag = "heading", .AutoSize = True, .Margin = New Padding(0, 6, 20, 0)})
        topBar.Controls.Add(btnAddStaff)
        topBar.Controls.Add(btnEditStaff)
        topBar.Controls.Add(btnToggle)
        topBar.Controls.Add(btnDeleteStaff)
        topBar.Controls.Add(btnPayrollHistory)
        topBar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
        topBar.Controls.Add(txtSearchStaff)
        Dim topPanel As New Panel() With {.Dock = DockStyle.Fill}
        topPanel.Controls.Add(gridStaff)
        topPanel.Controls.Add(topBar)

        Dim botBar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8)}
        botBar.Controls.Add(New Label() With {.Text = "Loans for selected employee", .Tag = "heading", .AutoSize = True, .Margin = New Padding(0, 6, 20, 0)})
        botBar.Controls.Add(btnGiveLoan)
        botBar.Controls.Add(btnRepay)
        botBar.Controls.Add(btnDeleteLoan)
        Dim botPanel As New Panel() With {.Dock = DockStyle.Fill}
        botPanel.Controls.Add(gridLoans)
        botPanel.Controls.Add(botBar)

        split.Controls.Add(topPanel, 0, 0)
        split.Controls.Add(botPanel, 0, 1)
        tp.Controls.Add(split)
        Return tp
    End Function

    ' ---- attendance tab ----
    ''' Admin view of clerk check-in/out — the record kept for whoever needs to see it.
    Private Function BuildAttendanceTab() As TabPage
        Dim tp As New TabPage("Attendance")
        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(10)}
        bar.Controls.Add(New Label() With {.Text = "From:", .AutoSize = True, .Margin = New Padding(0, 6, 4, 0)})
        bar.Controls.Add(dtpAttFrom)
        bar.Controls.Add(New Label() With {.Text = "To:", .AutoSize = True, .Margin = New Padding(12, 6, 4, 0)})
        bar.Controls.Add(dtpAttTo)
        bar.Controls.Add(btnAttRefresh)
        bar.Controls.Add(btnExportAtt)
        Dim host As New Panel() With {.Dock = DockStyle.Fill}
        host.Controls.Add(gridAttendance)
        host.Controls.Add(bar)
        tp.Controls.Add(host)
        Return tp
    End Function

    Private Sub LoadAttendance()
        gridAttendance.Bind(Attendance.ForDateRange(dtpAttFrom.Value.Date, dtpAttTo.Value.Date))
    End Sub

    Private Function SelectedEmployeeId() As Integer?
        If gridStaff.Grid.SelectedRows.Count = 0 Then Return Nothing
        Return CInt(gridStaff.Grid.SelectedRows(0).Cells("EmployeeID").Value)
    End Function

    Private Sub LoadStaff()
        Dim term = txtSearchStaff.Text.Trim()
        Dim p As New Dictionary(Of String, Object) From {{"@s", If(term = "", CObj(DBNull.Value), "%" & term & "%")}}
        gridStaff.Bind(DataAccess.GetTable(
            "SELECT e.EmployeeID, e.FullName, e.Position, e.Phone, e.StartedOn, e.MonthlySalary, " &
            "CASE WHEN e.IsActive=1 THEN 'Active' ELSE 'Inactive' END AS [Status], " &
            "ISNULL((SELECT SUM(Balance) FROM EmployeeLoans WHERE EmployeeID=e.EmployeeID AND Closed=0),0) AS LoanBalance " &
            "FROM Employees e " &
            "WHERE (@s IS NULL OR e.FullName LIKE @s OR e.Phone LIKE @s OR e.Position LIKE @s) " &
            "ORDER BY e.IsActive DESC, e.FullName", p), hiddenColumns:={"EmployeeID"})
    End Sub

    Private Sub LoadLoans()
        Dim eid = SelectedEmployeeId()
        If eid Is Nothing Then
            gridLoans.Bind(New DataTable())
            Return
        End If
        gridLoans.Bind(DataAccess.GetTable(
            "SELECT LoanID, LoanDate, Principal, Balance, " &
            "CASE WHEN Closed=1 THEN 'Cleared' ELSE 'Owing' END AS [Status], Note " &
            "FROM EmployeeLoans WHERE EmployeeID=@e ORDER BY LoanID DESC",
            New Dictionary(Of String, Object) From {{"@e", eid.Value}}), hiddenColumns:={"LoanID"})
    End Sub

    Private Sub EditEmployee(id As Integer?)
        Using f As New frmAddEmployee(id)
            If f.ShowDialog(FindForm()) = DialogResult.OK Then
                LoadStaff()
                AppUI.Toast("Saved.", AppUI.ToastKind.Success)
            End If
        End Using
    End Sub

    ''' Deletes an employee outright, with their payroll months, loans and
    ''' repayments — for clearing out test names before real data goes in.
    ''' (Use "Activate / deactivate" instead for someone who has left: that keeps
    ''' their history for the records.)
    Private Sub DeleteStaff_Click(sender As Object, e As EventArgs)
        Dim eid = SelectedEmployeeId()
        If eid Is Nothing Then Return
        Dim name = Convert.ToString(gridStaff.Grid.SelectedRows(0).Cells("FullName").Value)
        Dim counts = Staff.RecordCounts(eid.Value)

        If Not AppUI.Confirm(FindForm(),
            $"Delete {name} permanently?" & vbCrLf & vbCrLf &
            $"This also removes {counts.Months} payroll month(s) and {counts.Loans} loan record(s) with their repayments." & vbCrLf &
            "Salary expenses already posted to Finance are kept." & vbCrLf & vbCrLf &
            "This cannot be undone.", "Delete employee", "Delete", danger:=True) Then Return

        Try
            Staff.DeleteEmployee(eid.Value)
            AppUI.Toast($"{name} deleted.", AppUI.ToastKind.Success)
            LoadStaff()
            LoadLoans()
            LoadPayroll()
        Catch ex As Exception
            AppUI.Toast("Delete failed: " & ex.Message, AppUI.ToastKind.Error)
        End Try
    End Sub

    Private Sub DeleteLoan_Click(sender As Object, e As EventArgs)
        If gridLoans.Grid.SelectedRows.Count = 0 Then Return
        Dim loanId = CInt(gridLoans.Grid.SelectedRows(0).Cells("LoanID").Value)
        Dim principal = Convert.ToDecimal(gridLoans.Grid.SelectedRows(0).Cells("Principal").Value)
        If Not AppUI.Confirm(FindForm(),
            $"Delete this loan of {AppInfo.Money(principal)} and its repayment history?" & vbCrLf & vbCrLf &
            "This cannot be undone.", "Delete loan", "Delete", danger:=True) Then Return
        Try
            Staff.DeleteLoan(loanId)
            AppUI.Toast("Loan deleted.", AppUI.ToastKind.Success)
            LoadStaff()
            LoadLoans()
        Catch ex As Exception
            AppUI.Toast("Delete failed: " & ex.Message, AppUI.ToastKind.Error)
        End Try
    End Sub

    ''' Takes one employee off this payroll month (e.g. a test row).
    Private Sub RemovePayRow_Click(sender As Object, e As EventArgs)
        If gridPay.Grid.SelectedRows.Count = 0 Then
            AppUI.Info(FindForm(), "Select the payroll row to remove first.")
            Return
        End If
        Dim row = gridPay.Grid.SelectedRows(0)
        Dim id = CInt(row.Cells("EmpMonthID").Value)
        Dim who = Convert.ToString(row.Cells("FullName").Value)
        If AppUI.TryDelete(FindForm(), $"{who} from {AppInfo.PeriodLabel(PYear, PMonth)}",
                           "DELETE FROM EmployeeMonthly WHERE EmpMonthID=@id",
                           New Dictionary(Of String, Object) From {{"@id", id}}) Then
            LoadPayroll()
        End If
    End Sub

    Private Sub Toggle_Click(sender As Object, e As EventArgs)
        Dim eid = SelectedEmployeeId()
        If eid Is Nothing Then Return
        DataAccess.Execute("UPDATE Employees SET IsActive = 1 - IsActive WHERE EmployeeID=@e",
            New Dictionary(Of String, Object) From {{"@e", eid.Value}})
        LoadStaff()
    End Sub

    Private Sub GiveLoan_Click(sender As Object, e As EventArgs)
        Dim eid = SelectedEmployeeId()
        If eid Is Nothing Then Return
        Dim name = Convert.ToString(gridStaff.Grid.SelectedRows(0).Cells("FullName").Value)
        Using f As New frmMoneyPrompt($"Loan to {name}", "Loan amount", "Note (optional)")
            If f.ShowDialog(FindForm()) = DialogResult.OK AndAlso f.Amount > 0 Then
                DataAccess.Execute(
                    "INSERT INTO EmployeeLoans (EmployeeID, Principal, Balance, Note) VALUES (@e, @a, @a, @n)",
                    New Dictionary(Of String, Object) From {{"@e", eid.Value}, {"@a", f.Amount}, {"@n", f.NoteText}})
                AppUI.Toast($"Loan of {AppInfo.Money(f.Amount)} recorded for {name}.", AppUI.ToastKind.Success)
                LoadStaff()
                LoadLoans()
            End If
        End Using
    End Sub

    Private Sub Repay_Click(sender As Object, e As EventArgs)
        If gridLoans.Grid.SelectedRows.Count = 0 Then Return
        Dim loanId = CInt(gridLoans.Grid.SelectedRows(0).Cells("LoanID").Value)
        Dim bal = Convert.ToDecimal(gridLoans.Grid.SelectedRows(0).Cells("Balance").Value)
        Using f As New frmMoneyPrompt("Record repayment", "Amount paid back", "Note (optional)")
            If f.ShowDialog(FindForm()) = DialogResult.OK AndAlso f.Amount > 0 Then
                Dim pay = Math.Min(f.Amount, bal)
                DataAccess.Execute("INSERT INTO LoanRepayments (LoanID, Amount, Note) VALUES (@l, @a, @n)",
                    New Dictionary(Of String, Object) From {{"@l", loanId}, {"@a", pay}, {"@n", f.NoteText}})
                DataAccess.Execute("UPDATE EmployeeLoans SET Balance = Balance - @a, Closed = CASE WHEN Balance - @a <= 0 THEN 1 ELSE 0 END WHERE LoanID=@l",
                    New Dictionary(Of String, Object) From {{"@a", pay}, {"@l", loanId}})
                AppUI.Toast($"Repayment of {AppInfo.Money(pay)} recorded.", AppUI.ToastKind.Success)
                LoadStaff()
                LoadLoans()
            End If
        End Using
    End Sub

End Class
