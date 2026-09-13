Imports System.Windows.Forms
Imports System.Drawing

''' One employee's whole payroll record: every month they've been paid (or
''' not), newest first — how a payment can be traced beyond "just this month".
Public Class frmPayrollHistory
    Inherits Form

    Private ReadOnly grid As DataGridView = UiHelpers.NewGrid()

    Public Sub New(employeeId As Integer, employeeName As String)
        Text = employeeName & " — payroll history"
        Width = 640
        Height = 520
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        Dim head As New Label() With {.Dock = DockStyle.Top, .AutoSize = True, .Tag = "heading",
            .Padding = New Padding(16, 10, 16, 6), .Text = "Payroll history — " & employeeName}

        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(16)}
        Dim btnClose As New Button() With {.Text = "Close", .AutoSize = True, .DialogResult = DialogResult.OK}
        Dim btnExport As New Button() With {.Text = "Export CSV", .AutoSize = True}
        AddHandler btnExport.Click, Sub(s, e) AppUI.ExportCsv(grid, "payroll_history_" & employeeName, Me)
        bar.Controls.Add(btnClose)
        bar.Controls.Add(btnExport)
        CancelButton = btnClose

        Controls.Add(grid)
        Controls.Add(bar)
        Controls.Add(head)

        grid.DataSource = FriendlyRows(Payroll.HistoryForEmployee(employeeId))
        If grid.Columns.Contains("Paid") Then grid.Columns("Paid").HeaderText = "Paid?"

        UiHelpers.FitToScreen(Me)
        Theme.Apply(Me)
    End Sub

    ''' Numeric month → its name, for a history that reads naturally.
    Private Function FriendlyRows(t As Data.DataTable) As Data.DataTable
        If t.Columns.Contains("Month") Then
            Dim withNames As New Data.DataTable()
            withNames.Columns.Add("Period", GetType(String))
            For Each c In {"Position", "SalaryAmount", "LoanDeduction", "NetPay", "Paid", "PaidDate"}
                If t.Columns.Contains(c) Then withNames.Columns.Add(c, t.Columns(c).DataType)
            Next
            For Each r As Data.DataRow In t.Rows
                Dim newRow = withNames.NewRow()
                newRow("Period") = AppInfo.PeriodLabel(Convert.ToInt32(r("Year")), Convert.ToInt32(r("Month")))
                For Each c In {"Position", "SalaryAmount", "LoanDeduction", "NetPay", "Paid", "PaidDate"}
                    If t.Columns.Contains(c) Then newRow(c) = r(c)
                Next
                withNames.Rows.Add(newRow)
            Next
            Return withNames
        End If
        Return t
    End Function

End Class
