Imports System.Data
Imports System.Drawing
Imports System.Windows.Forms

''' Update many prices at once. Every product in one scrolling list with its
''' current and new price at each tier: type new prices straight in, or move a
''' whole category by a percentage with rounding, add a note ("September
''' review"), and save. Only lines that actually changed are saved and logged.
'''
''' One long editable list rather than pages of five: a price review is done
''' down the whole list, and edits must not be lost when a page turns.
Public Class frmPriceUpdate
    Inherits Form

    Private ReadOnly _userId As Integer
    Private ReadOnly _rows As DataTable
    Private ReadOnly _view As DataView
    Private ReadOnly grid As New DataGridView() With {
        .Dock = DockStyle.Fill, .AllowUserToAddRows = False, .AllowUserToDeleteRows = False,
        .SelectionMode = DataGridViewSelectionMode.CellSelect, .AutoGenerateColumns = True,
        .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill}
    Private ReadOnly cboCategory As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 170}
    Private ReadOnly numPercent As New NumericUpDown() With {.Minimum = -90, .Maximum = 500, .DecimalPlaces = 1, .Width = 80}
    Private ReadOnly chkDist As New CheckBox() With {.Text = "Distributor", .Checked = True, .AutoSize = True}
    Private ReadOnly chkWhole As New CheckBox() With {.Text = "Wholesaler", .Checked = True, .AutoSize = True}
    Private ReadOnly chkRetail As New CheckBox() With {.Text = "Retail", .Checked = True, .AutoSize = True}
    Private ReadOnly cboRound As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 120}
    Private ReadOnly btnApplyPct As New Button() With {.Text = "Apply to list", .AutoSize = True}
    Private ReadOnly txtNote As New TextBox() With {.Width = 320}
    Private ReadOnly lblChanged As New Label() With {.AutoSize = True, .Margin = New Padding(12, 10, 12, 0)}
    Private ReadOnly btnSave As New Button() With {.Text = "Save price changes", .Tag = "primary", .AutoSize = True}
    Private ReadOnly btnCancel As New Button() With {.Text = "Cancel", .AutoSize = True}

    Private Shared ReadOnly Tiers As String() = {"Distributor", "Wholesaler", "Retail"}

    Public Sub New(userId As Integer, Optional focusProductId As Integer? = Nothing)
        _userId = userId
        Text = $"Update prices — {Company.Current.DisplayName}"
        Width = 1150
        Height = 760
        StartPosition = FormStartPosition.CenterParent

        _rows = DataAccess.GetTable(
            "SELECT p.ProductID, c.Name AS Category, p.Name AS Product, p.Unit, " &
            "p.PriceDistributor AS DistributorNow, p.PriceWholesaler AS WholesalerNow, p.PriceRetail AS RetailNow " &
            "FROM Products p JOIN Categories c ON c.CategoryID = p.CategoryID WHERE p.IsActive = 1 ORDER BY c.Name, p.Name")
        For Each tier In Tiers
            _rows.Columns.Add(tier & "New", GetType(Decimal))
        Next
        For Each r As DataRow In _rows.Rows
            For Each tier In Tiers
                r(tier & "New") = r(tier & "Now")
            Next
        Next
        _view = New DataView(_rows)

        cboCategory.Items.Add("All categories")
        cboCategory.Items.AddRange(_rows.AsEnumerable().Select(Function(r) Convert.ToString(r("Category"))).Distinct().ToArray())
        cboCategory.SelectedIndex = 0
        cboRound.Items.AddRange({"No rounding", "Nearest ₦10", "Nearest ₦50", "Nearest ₦100"})
        cboRound.SelectedIndex = 2

        Dim filterRow As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(10, 10, 10, 0)}
        filterRow.Controls.Add(New Label() With {.Text = "Show:", .AutoSize = True, .Margin = New Padding(0, 8, 6, 0)})
        filterRow.Controls.Add(cboCategory)
        filterRow.Controls.Add(New Label() With {.Text = "   Change shown prices by", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
        filterRow.Controls.Add(numPercent)
        filterRow.Controls.Add(New Label() With {.Text = "%  on", .AutoSize = True, .Margin = New Padding(4, 8, 6, 0)})
        filterRow.Controls.AddRange(New Control() {chkDist, chkWhole, chkRetail, cboRound, btnApplyPct})

        Dim noteRow As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(10, 6, 10, 6)}
        noteRow.Controls.Add(New Label() With {.Text = "Note for the history (optional):", .AutoSize = True, .Margin = New Padding(0, 8, 6, 0)})
        noteRow.Controls.Add(txtNote)
        noteRow.Controls.Add(New Label() With {.Text = "Type new prices in the 'new' columns — changed prices turn bold.",
                                               .AutoSize = True, .Margin = New Padding(16, 8, 0, 0), .ForeColor = Theme.Current.TextMuted})

        Dim buttons As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(10)}
        buttons.Controls.AddRange(New Control() {btnCancel, btnSave, lblChanged})

        Controls.Add(grid)
        Controls.Add(noteRow)
        Controls.Add(filterRow)
        Controls.Add(buttons)

        grid.DataSource = _view
        AddHandler grid.DataBindingComplete, Sub(s, e) ShapeColumns()
        AddHandler grid.CellValidating, AddressOf Grid_CellValidating
        AddHandler grid.CellFormatting, AddressOf Grid_CellFormatting
        AddHandler grid.CellValueChanged, Sub(s, e) CountChanges()
        AddHandler grid.DataError, Sub(s, e) e.ThrowException = False
        AddHandler cboCategory.SelectedIndexChanged, Sub(s, e) FilterCategory()
        AddHandler btnApplyPct.Click, Sub(s, e) ApplyPercent()
        AddHandler btnSave.Click, AddressOf Save_Click
        AddHandler btnCancel.Click, Sub(s, e) Close()
        AddHandler Shown, Sub(s, e) FocusProduct(focusProductId)

        Theme.Apply(Me)
        Theme.ApplyGrid(grid)
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect
        UiHelpers.FitToScreen(Me)
        CountChanges()
    End Sub

    Private Sub ShapeColumns()
        If grid.Columns.Count = 0 Then Return
        grid.Columns("ProductID").Visible = False
        For Each col As DataGridViewColumn In grid.Columns
            col.ReadOnly = Not col.Name.EndsWith("New")
            col.SortMode = DataGridViewColumnSortMode.NotSortable
        Next
        grid.Columns("Product").FillWeight = 220
        For Each tier In Tiers
            With grid.Columns(tier & "Now")
                .HeaderText = tier & " now"
                .DefaultCellStyle.Format = "N2"
                .DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight
                .DefaultCellStyle.ForeColor = Theme.Current.TextMuted
            End With
            With grid.Columns(tier & "New")
                .HeaderText = tier & " new"
                .DefaultCellStyle.Format = "N2"
                .DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight
                .DefaultCellStyle.BackColor = Color.FromArgb(255, 252, 235)
            End With
        Next
        ' Now / new pairs side by side.
        Dim order = 4
        For Each tier In Tiers
            grid.Columns(tier & "Now").DisplayIndex = order
            grid.Columns(tier & "New").DisplayIndex = order + 1
            order += 2
        Next
    End Sub

    Private Sub Grid_CellValidating(sender As Object, e As DataGridViewCellValidatingEventArgs)
        If Not grid.Columns(e.ColumnIndex).Name.EndsWith("New") Then Return
        Dim text = Convert.ToString(e.FormattedValue).Replace("₦", "").Replace(",", "").Trim()
        Dim v As Decimal
        If Not Decimal.TryParse(text, v) OrElse v < 0 Then
            grid.Rows(e.RowIndex).ErrorText = "Enter a price of 0 or more."
            e.Cancel = True
        Else
            grid.Rows(e.RowIndex).ErrorText = ""
        End If
    End Sub

    Private Sub Grid_CellFormatting(sender As Object, e As DataGridViewCellFormattingEventArgs)
        If e.RowIndex < 0 Then Return
        Dim name = grid.Columns(e.ColumnIndex).Name
        If Not name.EndsWith("New") Then Return
        Dim row = DirectCast(grid.Rows(e.RowIndex).DataBoundItem, DataRowView)
        If row Is Nothing Then Return
        Dim tier = name.Substring(0, name.Length - 3)
        If Convert.ToDecimal(row(tier & "New")) <> Convert.ToDecimal(row(tier & "Now")) Then
            e.CellStyle.Font = New Font(grid.Font, FontStyle.Bold)
            e.CellStyle.ForeColor = Theme.Current.Primary
        End If
    End Sub

    Private Sub FilterCategory()
        _view.RowFilter = If(cboCategory.SelectedIndex <= 0, "",
                             $"Category = '{Convert.ToString(cboCategory.SelectedItem).Replace("'", "''")}'")
    End Sub

    Private Sub FocusProduct(productId As Integer?)
        If Not productId.HasValue Then Return
        For Each r As DataGridViewRow In grid.Rows
            If CInt(r.Cells("ProductID").Value) = productId.Value Then
                grid.CurrentCell = r.Cells("RetailNew")
                grid.FirstDisplayedScrollingRowIndex = r.Index
                Exit For
            End If
        Next
    End Sub

    Private Function RoundTo() As Decimal
        Select Case cboRound.SelectedIndex
            Case 1 : Return 10D
            Case 2 : Return 50D
            Case 3 : Return 100D
            Case Else : Return 0D
        End Select
    End Function

    ''' Moves the shown rows (the selected category, or all) from their current
    ''' price — applying twice doesn't compound.
    Private Sub ApplyPercent()
        grid.EndEdit()
        Dim chosen = Tiers.Where(Function(tier, i) {chkDist, chkWhole, chkRetail}(i).Checked).ToList()
        If chosen.Count = 0 Then
            AppUI.Info(Me, "Tick at least one price tier to change.")
            Return
        End If
        For Each rv As DataRowView In _view
            For Each tier In chosen
                rv(tier & "New") = PriceBook.Adjusted(Convert.ToDecimal(rv(tier & "Now")), numPercent.Value, RoundTo())
            Next
        Next
        grid.Invalidate()
        CountChanges()
    End Sub

    Private Function Changes() As List(Of PriceBook.PriceUpdate)
        Return _rows.AsEnumerable().
            Where(Function(r) Tiers.Any(Function(tier) Convert.ToDecimal(r(tier & "New")) <> Convert.ToDecimal(r(tier & "Now")))).
            Select(Function(r) New PriceBook.PriceUpdate With {
                .ProductID = Convert.ToInt32(r("ProductID")),
                .Distributor = Convert.ToDecimal(r("DistributorNew")),
                .Wholesaler = Convert.ToDecimal(r("WholesalerNew")),
                .Retail = Convert.ToDecimal(r("RetailNew"))}).ToList()
    End Function

    Private Sub CountChanges()
        Dim n = Changes().Count
        lblChanged.Text = If(n = 0, "No prices changed yet", $"{n} product(s) with new prices")
        btnSave.Enabled = n > 0
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        If Not grid.EndEdit() Then Return
        Dim updates = Changes()
        If updates.Count = 0 Then Return
        Try
            Dim saved = PriceBook.Apply(updates, _userId, txtNote.Text)
            AppUI.Toast($"New prices saved for {saved} product(s).", AppUI.ToastKind.Success)
            DialogResult = DialogResult.OK
            Close()
        Catch ex As Exception
            AppUI.Toast("Prices were not saved: " & ex.Message, AppUI.ToastKind.Error)
        End Try
    End Sub

End Class
