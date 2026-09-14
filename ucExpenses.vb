Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data

''' Every expense spelled out (rent, utilities, logistics, salary payments, …),
''' filterable by month/year, with add / delete and CSV / Excel export.
Public Class ucExpenses
    Inherits UserControl

    Private ReadOnly currentUserId As Integer
    Private ReadOnly grid As New PagedGrid() With {.PageSize = 5}
    Private cboYear As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 90}
    Private cboMonth As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 130}
    Private cboCategory As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 140}
    Private btnAdd As New Button() With {.Text = "+ Add expense", .Tag = "primary", .AutoSize = True}
    Private btnDelete As New Button() With {.Text = "Delete", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private btnExport As New Button() With {.Text = "Export ▾", .AutoSize = True}
    Private ReadOnly exportMenu As New ContextMenuStrip()
    Private lblTotal As New Label() With {.AutoSize = True, .Tag = "keepfont", .Font = New Font("Segoe UI", 11, FontStyle.Bold)}
    Private txtSearch As New TextBox() With {.Width = 160}

    Public Sub New(userId As Integer)
        currentUserId = userId

        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(12)}
        bar.Controls.Add(cboYear)
        bar.Controls.Add(cboMonth)
        bar.Controls.Add(cboCategory)
        bar.Controls.Add(btnAdd)
        bar.Controls.Add(btnDelete)
        bar.Controls.Add(btnExport)
        bar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
        bar.Controls.Add(txtSearch)
        bar.Controls.Add(lblTotal)
        Controls.Add(grid)
        Controls.Add(bar)

        Dim thisYear = DateTime.Now.Year
        For y = thisYear To thisYear - 6 Step -1
            cboYear.Items.Add(y)
        Next
        cboYear.SelectedItem = thisYear
        cboMonth.Items.Add("Whole year")
        For m = 1 To 12
            cboMonth.Items.Add(AppInfo.MonthName(m))
        Next
        cboMonth.SelectedIndex = DateTime.Now.Month
        cboCategory.Items.AddRange({"All categories", "Rent", "Salaries", "Utilities", "Logistics", "Maintenance", "Other"})
        cboCategory.SelectedIndex = 0

        AddHandler cboYear.SelectedIndexChanged, Sub(s, e) LoadGrid()
        AddHandler cboMonth.SelectedIndexChanged, Sub(s, e) LoadGrid()
        AddHandler cboCategory.SelectedIndexChanged, Sub(s, e) LoadGrid()
        AddHandler txtSearch.TextChanged, Sub(s, e) LoadGrid()
        AddHandler grid.Grid.SelectionChanged, Sub(s, e) btnDelete.Enabled = grid.Grid.SelectedRows.Count > 0
        AddHandler btnAdd.Click, AddressOf AddExpense_Click
        AddHandler btnDelete.Click, AddressOf Delete_Click
        exportMenu.Items.Add("Expenses (CSV)…", Nothing,
            Sub(s, e) AppUI.ExportCsv(grid.AllRows(), "expenses", FindForm()))
        exportMenu.Items.Add("Expenses (Excel)…", Nothing,
            Sub(s, e) Exporter.SaveExcel(grid.AllRows(), "Expenses", Company.Current.FilePrefix & "_expenses", FindForm()))
        AddHandler btnExport.Click, Sub(s, e) exportMenu.Show(btnExport, New Point(0, btnExport.Height))
        AddHandler Me.Load, Sub(s, e) LoadGrid()
    End Sub

    Private Sub LoadGrid()
        Dim month = If(cboMonth.SelectedIndex <= 0, CType(Nothing, Integer?), cboMonth.SelectedIndex)
        Dim term = txtSearch.Text.Trim()
        Dim p As New Dictionary(Of String, Object) From {
            {"@y", CInt(cboYear.SelectedItem)},
            {"@m", If(month.HasValue, CObj(month.Value), DBNull.Value)},
            {"@cat", If(cboCategory.SelectedIndex = 0, CObj(DBNull.Value), cboCategory.Text)},
            {"@s", If(term = "", CObj(DBNull.Value), "%" & term & "%")}}
        Dim t = DataAccess.GetTable(
            "SELECT ExpenseID, ExpenseDate, Category, Amount, Note FROM Expenses " &
            "WHERE YEAR(ExpenseDate)=@y AND (@m IS NULL OR MONTH(ExpenseDate)=@m) AND (@cat IS NULL OR Category=@cat) " &
            "AND (@s IS NULL OR Category LIKE @s OR Note LIKE @s) " &
            "ORDER BY ExpenseDate DESC", p)
        grid.Bind(t, hiddenColumns:={"ExpenseID"})
        Dim total = t.AsEnumerable().Sum(Function(r) Convert.ToDecimal(r("Amount")))
        lblTotal.Text = $"Total for {AppInfo.PeriodLabel(CInt(cboYear.SelectedItem), month)}: {AppInfo.Money(total)}"
    End Sub

    Private Sub AddExpense_Click(sender As Object, e As EventArgs)
        Using f As New frmAddExpense()
            If f.ShowDialog() = DialogResult.OK Then
                DataAccess.Execute(
                    "INSERT INTO Expenses (Category, ExpenseDate, Amount, Note, CreatedByUserID) VALUES (@c,@d,@a,@n,@u)",
                    New Dictionary(Of String, Object) From {
                        {"@c", f.Category}, {"@d", f.ExpenseDate}, {"@a", f.Amount}, {"@n", f.Note}, {"@u", currentUserId}})
                AppUI.Toast("Expense recorded.", AppUI.ToastKind.Success)
                LoadGrid()
            End If
        End Using
    End Sub

    Private Sub Delete_Click(sender As Object, e As EventArgs)
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Dim id = CInt(grid.Grid.SelectedRows(0).Cells("ExpenseID").Value)
        Dim label = $"{grid.Grid.SelectedRows(0).Cells("Category").Value} expense of {AppInfo.Money(grid.Grid.SelectedRows(0).Cells("Amount").Value)}"
        If AppUI.TryDelete(FindForm(), label, "DELETE FROM Expenses WHERE ExpenseID=@id",
                           New Dictionary(Of String, Object) From {{"@id", id}}) Then
            LoadGrid()
        End If
    End Sub

End Class
