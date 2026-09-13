Imports System.Windows.Forms

''' Add / edit an employee (name, position, phone, start date, monthly salary).
''' Monthly salary here is what "Generate month" uses to fill a brand-new
''' employee's first payroll row, so it's never left at zero unless the admin
''' left it blank on purpose.
Public Class frmAddEmployee
    Inherits Form

    Private ReadOnly _id As Integer?
    Private txtName As New TextBox()
    Private txtPosition As New TextBox()
    Private txtPhone As New TextBox()
    Private dtpStart As New DateTimePicker() With {.Format = DateTimePickerFormat.Short}
    Private numSalary As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2, .ThousandsSeparator = True}

    Public Sub New(Optional employeeId As Integer? = Nothing)
        _id = employeeId
        Text = If(_id.HasValue, "Edit employee", "Add employee")
        Width = 380
        Height = 340
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        Dim t = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(t, "Full name", txtName)
        UiHelpers.AddLabeled(t, "Position", txtPosition)
        UiHelpers.AddLabeled(t, "Phone", txtPhone)
        UiHelpers.AddLabeled(t, "Started on", dtpStart)
        UiHelpers.AddLabeled(t, "Monthly salary", numSalary)
        Controls.Add(t)
        UiHelpers.AddOkCancelRow(Me, "Save", AddressOf Save_Click)

        If _id.HasValue Then
            Dim r = DataAccess.GetTable("SELECT FullName, Position, Phone, StartedOn, MonthlySalary FROM Employees WHERE EmployeeID=@id",
                New Dictionary(Of String, Object) From {{"@id", _id.Value}}).Rows(0)
            txtName.Text = Convert.ToString(r("FullName"))
            txtPosition.Text = Convert.ToString(r("Position"))
            txtPhone.Text = Convert.ToString(r("Phone"))
            dtpStart.Value = Convert.ToDateTime(r("StartedOn"))
            numSalary.Value = Convert.ToDecimal(r("MonthlySalary"))
        End If
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        If String.IsNullOrWhiteSpace(txtName.Text) OrElse String.IsNullOrWhiteSpace(txtPosition.Text) Then
            AppUI.Info(Me, "Name and position are required.")
            Return
        End If
        Dim p As New Dictionary(Of String, Object) From {
            {"@n", txtName.Text.Trim()}, {"@pos", txtPosition.Text.Trim()},
            {"@ph", If(String.IsNullOrWhiteSpace(txtPhone.Text), CObj(DBNull.Value), txtPhone.Text.Trim())},
            {"@st", dtpStart.Value.Date}, {"@sal", numSalary.Value}}
        If _id.HasValue Then
            p("@id") = _id.Value
            DataAccess.Execute("UPDATE Employees SET FullName=@n, Position=@pos, Phone=@ph, StartedOn=@st, MonthlySalary=@sal WHERE EmployeeID=@id", p)
        Else
            DataAccess.Execute("INSERT INTO Employees (FullName, Position, Phone, StartedOn, MonthlySalary) VALUES (@n,@pos,@ph,@st,@sal)", p)
        End If
        DialogResult = DialogResult.OK
        Close()
    End Sub

End Class
