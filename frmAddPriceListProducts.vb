Imports System.Data
Imports System.Drawing
Imports System.Windows.Forms

''' Type a long price list in one sitting: one row per product — name,
''' category, unit and the three prices. A new empty row appears as each one is
''' filled, like a spreadsheet. Saving adds them all, each with its own product
''' code and barcode.
Public Class frmAddPriceListProducts
    Inherits Form

    Private ReadOnly _userId As Integer
    Private ReadOnly grid As New DataGridView() With {
        .Dock = DockStyle.Fill, .AllowUserToAddRows = True, .AllowUserToDeleteRows = True,
        .AutoGenerateColumns = False, .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        .SelectionMode = DataGridViewSelectionMode.CellSelect}
    Private ReadOnly colName As New DataGridViewTextBoxColumn() With {.Name = "ProductName", .HeaderText = "Product name", .FillWeight = 260}
    Private ReadOnly colCategory As New DataGridViewComboBoxColumn() With {.Name = "Category", .HeaderText = "Category", .FillWeight = 120, .FlatStyle = FlatStyle.Flat}
    Private ReadOnly colUnit As New DataGridViewComboBoxColumn() With {.Name = "Unit", .HeaderText = "Unit", .FillWeight = 80, .FlatStyle = FlatStyle.Flat}
    Private ReadOnly txtNote As New TextBox() With {.Width = 320}
    Private ReadOnly lblCount As New Label() With {.AutoSize = True, .Margin = New Padding(12, 10, 12, 0)}
    Private ReadOnly btnSave As New Button() With {.Text = "Add to price list", .Tag = "primary", .AutoSize = True}
    Private ReadOnly btnCancel As New Button() With {.Text = "Cancel", .AutoSize = True}

    Private Shared ReadOnly PriceColumns As String() = {"Distributor", "Wholesaler", "Retail"}
    Public Shared ReadOnly Property Units As String() = {"Bag", "Pack", "Piece", "Kg", "Carton", "Bottle", "Tin", "Sachet"}

    Public Sub New(userId As Integer)
        _userId = userId
        Text = $"Add products to the price list — {Company.Current.DisplayName}"
        Width = 1050
        Height = 680
        StartPosition = FormStartPosition.CenterParent

        Dim categories = DataAccess.GetTable("SELECT CategoryID, Name FROM Categories ORDER BY Name")
        colCategory.DataSource = categories
        colCategory.DisplayMember = "Name"
        colCategory.ValueMember = "CategoryID"
        colUnit.Items.AddRange(Units)
        grid.Columns.AddRange(colName, colCategory, colUnit)
        For Each tier In PriceColumns
            grid.Columns.Add(New DataGridViewTextBoxColumn() With {
                .Name = tier, .HeaderText = $"{tier} price (₦)", .FillWeight = 110,
                .DefaultCellStyle = New DataGridViewCellStyle() With {.Alignment = DataGridViewContentAlignment.MiddleRight, .Format = "N2"},
                .ValueType = GetType(Decimal)})
        Next

        Dim intro As New Label() With {
            .Dock = DockStyle.Top, .AutoSize = False, .Height = 44, .Padding = New Padding(12, 12, 12, 0),
            .ForeColor = Theme.Current.TextMuted,
            .Text = "One row per product. A new row opens as you type. Each product gets its own code and barcode; stock is added later when goods are produced or received."}
        Dim noteRow As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(10, 4, 10, 6)}
        noteRow.Controls.Add(New Label() With {.Text = "Note for the history (optional):", .AutoSize = True, .Margin = New Padding(0, 8, 6, 0)})
        noteRow.Controls.Add(txtNote)
        Dim buttons As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(10)}
        buttons.Controls.AddRange(New Control() {btnCancel, btnSave, lblCount})

        Controls.Add(grid)
        Controls.Add(noteRow)
        Controls.Add(intro)
        Controls.Add(buttons)

        AddHandler grid.DefaultValuesNeeded, Sub(s, e)
                                                 If categories.Rows.Count > 0 Then e.Row.Cells("Category").Value = categories.Rows(0)("CategoryID")
                                                 e.Row.Cells("Unit").Value = "Bag"
                                                 For Each tier In PriceColumns
                                                     e.Row.Cells(tier).Value = 0D
                                                 Next
                                             End Sub
        AddHandler grid.CellValidating, AddressOf Grid_CellValidating
        AddHandler grid.CellValueChanged, Sub(s, e) CountRows()
        AddHandler grid.RowsRemoved, Sub(s, e) CountRows()
        AddHandler grid.DataError, Sub(s, e) e.ThrowException = False
        AddHandler btnSave.Click, AddressOf Save_Click
        AddHandler btnCancel.Click, Sub(s, e) Close()

        Theme.Apply(Me)
        Theme.ApplyGrid(grid)
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect
        UiHelpers.FitToScreen(Me)
        CountRows()
    End Sub

    Private Sub Grid_CellValidating(sender As Object, e As DataGridViewCellValidatingEventArgs)
        Dim name = grid.Columns(e.ColumnIndex).Name
        If Not PriceColumns.Contains(name) Then Return
        Dim raw = Convert.ToString(e.FormattedValue).Replace("₦", "").Replace(",", "").Trim()
        If raw = "" Then Return
        Dim v As Decimal
        If Not Decimal.TryParse(raw, v) OrElse v < 0 Then
            grid.Rows(e.RowIndex).ErrorText = "Enter a price of 0 or more."
            e.Cancel = True
        Else
            grid.Rows(e.RowIndex).ErrorText = ""
        End If
    End Sub

    ''' The typed rows as products; the empty "new row" at the bottom is skipped.
    Friend Function Entered() As List(Of PriceBook.NewProduct)
        Dim list As New List(Of PriceBook.NewProduct)
        For Each r As DataGridViewRow In grid.Rows
            If r.IsNewRow Then Continue For
            Dim name = Convert.ToString(r.Cells("ProductName").Value).Trim()
            If name = "" Then Continue For
            list.Add(New PriceBook.NewProduct With {
                .Name = name,
                .CategoryID = Convert.ToInt32(If(r.Cells("Category").Value, 0)),
                .Unit = Convert.ToString(r.Cells("Unit").Value),
                .Distributor = Price(r.Cells("Distributor").Value),
                .Wholesaler = Price(r.Cells("Wholesaler").Value),
                .Retail = Price(r.Cells("Retail").Value)})
        Next
        Return list
    End Function

    Private Shared Function Price(value As Object) As Decimal
        Dim v As Decimal
        Return If(value IsNot Nothing AndAlso Decimal.TryParse(Convert.ToString(value).Replace(",", ""), v), v, 0D)
    End Function

    Private Sub CountRows()
        Dim n = Entered().Count
        lblCount.Text = If(n = 0, "No products typed yet", $"{n} product(s) ready to add")
        btnSave.Enabled = n > 0
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        If Not grid.EndEdit() Then Return
        Dim products = Entered()
        If products.Count = 0 Then Return
        Dim unpriced = products.Where(Function(p) p.Distributor = 0 OrElse p.Wholesaler = 0 OrElse p.Retail = 0).Select(Function(p) p.Name).ToList()
        If unpriced.Count > 0 AndAlso Not AppUI.Confirm(Me,
                $"{unpriced.Count} product(s) have a price of 0 at some tier ({String.Join(", ", unpriced.Take(3))}{If(unpriced.Count > 3, "…", "")})." &
                vbCrLf & vbCrLf & "They're left off price lists at that tier until priced. Add them anyway?",
                "Missing prices", "Add anyway") Then Return
        Try
            Dim ids = PriceBook.AddProducts(products, _userId, txtNote.Text)
            AppUI.Toast($"Added {ids.Count} product(s) to the price list.", AppUI.ToastKind.Success)
            DialogResult = DialogResult.OK
            Close()
        Catch ex As ArgumentException
            AppUI.Info(Me, ex.Message)
        Catch ex As Exception
            AppUI.Toast("Nothing was added: " & ex.Message, AppUI.ToastKind.Error)
        End Try
    End Sub

End Class
