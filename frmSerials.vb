Imports System.Data
Imports System.Windows.Forms

''' Serial-number tracking for the products that carry it: book units in, see
''' what's on the shelf, look one up, write one off, and find where the serial
''' register and the stock count disagree.
'''
''' Three tabs rather than one crowded screen, because they're three different
''' jobs done by different people at different times — receiving is a goods-in
''' task, look-up is a warranty-desk task, and the discrepancy report is a
''' stock-take task.
Public Class frmSerials
    Inherits Form

    Private ReadOnly currentUserId As Integer
    Private cboProduct As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 300}
    Private cboWarehouse As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 180}
    Private txtSerials As New TextBox() With {.Multiline = True, .Height = 150, .Width = 420, .ScrollBars = ScrollBars.Vertical}
    Private btnReceive As New Button() With {.Text = "Book these in", .Tag = "primary", .AutoSize = True}
    Private lblReceiveNote As New Label() With {.AutoSize = True, .MaximumSize = New Drawing.Size(420, 0)}

    Private ReadOnly pgAvailable As New PagedGrid() With {.PageSize = 5}
    Private lblAvailable As New Label() With {.AutoSize = True, .Tag = "keepfont", .Dock = DockStyle.Top, .Padding = New Padding(8)}
    Private txtLookup As New TextBox() With {.Width = 220}
    Private btnLookup As New Button() With {.Text = "Find", .AutoSize = True}
    Private btnWriteOff As New Button() With {.Text = "Write off", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private ReadOnly pgHistory As New PagedGrid() With {.PageSize = 5}

    Private ReadOnly pgDiscrepancies As New PagedGrid() With {.PageSize = 5}
    Private lblDiscrepancy As New Label() With {.AutoSize = True, .Dock = DockStyle.Top, .Padding = New Padding(8), .MaximumSize = New Drawing.Size(700, 0)}

    Public Sub New(userId As Integer)
        currentUserId = userId
        Text = "Serial numbers"
        Width = 820
        Height = 640
        StartPosition = FormStartPosition.CenterParent

        LoadProducts()
        cboWarehouse.DataSource = DataAccess.GetTable("SELECT WarehouseID, Name FROM Warehouses ORDER BY Name")
        cboWarehouse.DisplayMember = "Name"
        cboWarehouse.ValueMember = "WarehouseID"

        Dim tabs As New TabControl() With {.Dock = DockStyle.Fill}
        tabs.TabPages.Add(BuildReceiveTab())
        tabs.TabPages.Add(BuildLookupTab())
        tabs.TabPages.Add(BuildDiscrepancyTab())
        Controls.Add(tabs)
        UiHelpers.AddOkCancelRow(Me, "Close", Sub(s, e) Close())

        AddHandler cboProduct.SelectedIndexChanged, Sub(s, e) RefreshAvailable()
        AddHandler btnReceive.Click, AddressOf Receive_Click
        AddHandler btnLookup.Click, Sub(s, e) Lookup()
        AddHandler txtLookup.KeyDown, Sub(s, e)
                                          If e.KeyCode = Keys.Enter Then
                                              e.SuppressKeyPress = True
                                              Lookup()
                                          End If
                                      End Sub
        AddHandler btnWriteOff.Click, AddressOf WriteOff_Click
        AddHandler pgAvailable.Grid.SelectionChanged, Sub(s, e) btnWriteOff.Enabled = pgAvailable.Grid.SelectedRows.Count > 0

        AddHandler Me.Load, Sub(s, e)
                                RefreshAvailable()
                                RefreshDiscrepancies()
                            End Sub
        UiHelpers.FitToScreen(Me)
        Theme.Apply(Me)
    End Sub

    ''' Only the products actually set up to track serials — offering the rest
    ''' would invite booking serials against something that doesn't have them.
    Private Sub LoadProducts()
        cboProduct.DataSource = DataAccess.GetTable(
            "SELECT ProductID, Name FROM Products WHERE IsActive = 1 AND TracksSerial = 1 ORDER BY Name")
        cboProduct.DisplayMember = "Name"
        cboProduct.ValueMember = "ProductID"
    End Sub

    Private Function SelectedProductId() As Integer?
        Dim row = TryCast(cboProduct.SelectedItem, DataRowView)
        If row Is Nothing Then Return Nothing
        Return CInt(row("ProductID"))
    End Function

    ' ===== receive =====

    Private Function BuildReceiveTab() As TabPage
        Dim tp As New TabPage("Book in")
        Dim host As New Panel() With {.Dock = DockStyle.Fill, .Padding = New Padding(16), .AutoScroll = True}

        Dim form As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .FlowDirection = FlowDirection.TopDown, .WrapContents = False}
        form.Controls.Add(New Label() With {.Text = "Product", .AutoSize = True})
        form.Controls.Add(cboProduct)
        form.Controls.Add(New Label() With {.Text = "Warehouse", .AutoSize = True, .Margin = New Padding(0, 10, 0, 0)})
        form.Controls.Add(cboWarehouse)
        form.Controls.Add(New Label() With {
            .Text = "Serial numbers — one per line, or scan them straight in:",
            .AutoSize = True, .Margin = New Padding(0, 10, 0, 0)})
        form.Controls.Add(txtSerials)
        form.Controls.Add(btnReceive)
        lblReceiveNote.ForeColor = Theme.Current.TextMuted
        lblReceiveNote.Text = "A serial already on file is rejected — a duplicate means a typo or a counterfeit, " &
                              "and the whole batch is refused rather than half-booked."
        form.Controls.Add(lblReceiveNote)

        host.Controls.Add(form)
        tp.Controls.Add(host)
        Return tp
    End Function

    Private Sub Receive_Click(sender As Object, e As EventArgs)
        Dim productId = SelectedProductId()
        If productId Is Nothing Then
            AppUI.Info(Me, "Pick a serial-tracked product first.")
            Return
        End If
        Dim warehouse = TryCast(cboWarehouse.SelectedItem, DataRowView)
        If warehouse Is Nothing Then Return

        Dim numbers = Serials.Split(txtSerials.Text)
        If numbers.Count = 0 Then
            AppUI.Info(Me, "Enter at least one serial number.")
            Return
        End If

        Dim problem = Serials.Receive(productId.Value, CInt(warehouse("WarehouseID")), numbers)
        If problem <> "" Then
            AppUI.Info(Me, problem, "Nothing was booked in")
            Return
        End If

        AppUI.Toast($"Booked in {numbers.Count} unit(s).", AppUI.ToastKind.Success)
        txtSerials.Clear()
        RefreshAvailable()
        RefreshDiscrepancies()
    End Sub

    ' ===== look up =====

    Private Function BuildLookupTab() As TabPage
        Dim tp As New TabPage("On the shelf")

        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8)}
        bar.Controls.Add(New Label() With {.Text = "Serial:", .AutoSize = True, .Margin = New Padding(0, 8, 6, 0)})
        bar.Controls.Add(txtLookup)
        bar.Controls.Add(btnLookup)
        bar.Controls.Add(btnWriteOff)

        Dim split As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .RowCount = 2, .ColumnCount = 1}
        split.RowStyles.Add(New RowStyle(SizeType.Percent, 55))
        split.RowStyles.Add(New RowStyle(SizeType.Percent, 45))

        Dim top As New Panel() With {.Dock = DockStyle.Fill}
        top.Controls.Add(pgAvailable)
        top.Controls.Add(lblAvailable)

        Dim bottom As New Panel() With {.Dock = DockStyle.Fill}
        bottom.Controls.Add(pgHistory)
        bottom.Controls.Add(New Label() With {
            .Text = "History of the serial searched for", .Dock = DockStyle.Top,
            .Tag = "heading", .Padding = New Padding(8), .AutoSize = True})

        split.Controls.Add(top, 0, 0)
        split.Controls.Add(bottom, 0, 1)

        Dim host As New Panel() With {.Dock = DockStyle.Fill}
        host.Controls.Add(split)
        host.Controls.Add(bar)
        tp.Controls.Add(host)
        Return tp
    End Function

    Private Sub RefreshAvailable()
        Dim productId = SelectedProductId()
        If productId Is Nothing Then
            pgAvailable.Bind(New DataTable())
            lblAvailable.Text = "No serial-tracked products set up yet."
            Return
        End If
        pgAvailable.Bind(Serials.Available(productId.Value), hiddenColumns:={"SerialID", "WarehouseID"})
        lblAvailable.Text = $"{Serials.AvailableCount(productId.Value):#,0} unit(s) on the shelf, oldest first — " &
                            "hand them out in this order so nothing ages in a corner."
    End Sub

    Private Sub Lookup()
        Dim productId = SelectedProductId()
        If productId Is Nothing OrElse txtLookup.Text.Trim() = "" Then Return

        Dim history = Serials.History(productId.Value, txtLookup.Text.Trim())
        pgHistory.Bind(history)
        If history.Rows.Count = 0 Then
            AppUI.Toast($"No unit on file with serial {txtLookup.Text.Trim()}.", AppUI.ToastKind.Info)
        End If
    End Sub

    Private Sub WriteOff_Click(sender As Object, e As EventArgs)
        Dim productId = SelectedProductId()
        If productId Is Nothing OrElse pgAvailable.Grid.SelectedRows.Count = 0 Then Return
        Dim serial = Convert.ToString(pgAvailable.Grid.SelectedRows(0).Cells("SerialNumber").Value)

        Using prompt As New frmMoneyPrompt($"Write off {serial}", "", "Reason (damaged, lost, returned to supplier…)")
            If prompt.ShowDialog(Me) <> DialogResult.OK Then Return
            Dim problem = Serials.WriteOff(productId.Value, serial, prompt.NoteText)
            If problem <> "" Then
                AppUI.Info(Me, problem)
                Return
            End If
        End Using

        AppUI.Toast($"{serial} written off.", AppUI.ToastKind.Success)
        RefreshAvailable()
        RefreshDiscrepancies()
    End Sub

    ' ===== discrepancies =====

    Private Function BuildDiscrepancyTab() As TabPage
        Dim tp As New TabPage("Discrepancies")
        lblDiscrepancy.ForeColor = Theme.Current.TextMuted
        lblDiscrepancy.Text = "Where the counted stock and the serial register disagree. A difference means " &
                              "units were moved without their serials being booked, or serials were booked for " &
                              "units that never arrived — either way the shelf and the records tell different stories."

        Dim host As New Panel() With {.Dock = DockStyle.Fill}
        host.Controls.Add(pgDiscrepancies)
        host.Controls.Add(lblDiscrepancy)
        tp.Controls.Add(host)
        Return tp
    End Function

    Private Sub RefreshDiscrepancies()
        pgDiscrepancies.Bind(Serials.Discrepancies(), hiddenColumns:={"ProductID"})
    End Sub

End Class
