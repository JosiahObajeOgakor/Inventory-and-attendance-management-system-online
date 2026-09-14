Imports System.Windows.Forms

''' Invoice list + "Mark paid" action. Est. profit column only for Admin,
''' matching the HTML prototype. New invoices are created via frmNewInvoice.
Public Class ucInvoices
    Inherits UserControl

    Private ReadOnly currentUserId As Integer
    Private ReadOnly isAdmin As Boolean
    Private ReadOnly grid As New PagedGrid() With {.PageSize = 5}
    Private txtSearch As New TextBox() With {.Width = 200}
    ' Scan the code off a printed receipt to pull that sale straight back up —
    ' a return or a query at the counter, without hunting through the list.
    Private txtScanReceipt As New TextBox() With {.Width = 200}
    Private btnNewInvoice As New Button() With {.Text = "+ New invoice", .Tag = "primary", .AutoSize = True}
    Private btnReceipt As New Button() With {.Text = "View receipt / PDF", .AutoSize = True, .Enabled = False}
    Private btnExport As New Button() With {.Text = "Export CSV", .AutoSize = True}
    Private btnDelete As New Button() With {.Text = "Delete invoice", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private toolbar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(12)}

    Public Sub New(userId As Integer, isAdminUser As Boolean)
        currentUserId = userId
        isAdmin = isAdminUser
        toolbar.Controls.Add(btnNewInvoice)
        toolbar.Controls.Add(btnReceipt)
        toolbar.Controls.Add(btnExport)
        If isAdmin Then toolbar.Controls.Add(btnDelete)
        toolbar.Controls.Add(txtSearch)
        toolbar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(6, 8, 6, 0)})
        toolbar.Controls.Add(txtScanReceipt)
        toolbar.Controls.Add(New Label() With {.Text = "Scan receipt:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
        Controls.Add(grid)
        Controls.Add(toolbar)
        AddHandler btnNewInvoice.Click, AddressOf btnNewInvoice_Click
        AddHandler btnReceipt.Click, Sub(s, e) OpenReceipt()
        AddHandler btnExport.Click, Sub(s, e) AppUI.ExportCsv(grid.AllRows(), "invoices", FindForm())
        AddHandler btnDelete.Click, AddressOf btnDelete_Click
        AddHandler txtSearch.TextChanged, Sub(s, e) LoadGrid()
        AddHandler txtScanReceipt.KeyDown, AddressOf ScanReceipt_KeyDown
        AddHandler grid.Grid.SelectionChanged, Sub(s, e)
                                              Dim has = grid.Grid.SelectedRows.Count > 0
                                              btnReceipt.Enabled = has
                                              btnDelete.Enabled = has
                                          End Sub
        AddHandler grid.Grid.CellDoubleClick, Sub(s, e) OpenReceipt()
        AddHandler Me.Load, Sub(s, e) LoadGrid()
    End Sub

    Private Sub btnDelete_Click(sender As Object, e As EventArgs)
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Dim row = grid.Grid.SelectedRows(0)
        Dim invoiceId = CInt(row.Cells("InvoiceID").Value)
        Dim invoiceNumber = Convert.ToString(row.Cells("InvoiceNumber").Value)
        If Not AppUI.Confirm(FindForm(),
            $"Delete invoice {invoiceNumber}?" & vbCrLf & vbCrLf &
            "Its line items, payments and ledger entries are removed too. " &
            "Stock already deducted is NOT added back — adjust it manually if needed." & vbCrLf & vbCrLf &
            "This cannot be undone.", "Delete invoice", "Delete", danger:=True) Then Return
        Try
            DataAccess.ExecuteTransaction(New List(Of (Sql As String, Params As Dictionary(Of String, Object))) From {
                ("DELETE FROM Payments WHERE InvoiceID = @id", New Dictionary(Of String, Object) From {{"@id", invoiceId}}),
                ("DELETE FROM InvoiceItems WHERE InvoiceID = @id", New Dictionary(Of String, Object) From {{"@id", invoiceId}}),
                ("DELETE FROM Waybills WHERE InvoiceID = @id", New Dictionary(Of String, Object) From {{"@id", invoiceId}}),
                ("DELETE FROM RebateEntries WHERE InvoiceID = @id", New Dictionary(Of String, Object) From {{"@id", invoiceId}}),
                ("DELETE FROM Ledger WHERE Reference = @ref", New Dictionary(Of String, Object) From {{"@ref", invoiceNumber}}),
                ("DELETE FROM Invoices WHERE InvoiceID = @id", New Dictionary(Of String, Object) From {{"@id", invoiceId}})
            })
            AppUI.Toast($"Invoice {invoiceNumber} deleted.", AppUI.ToastKind.Success)
            LoadGrid()
        Catch ex As Exception
            AppUI.Toast("Delete failed: " & ex.Message, AppUI.ToastKind.Error)
        End Try
    End Sub

    ''' A receipt scanned at the counter opens that sale's receipt directly.
    ''' Accepts the plain invoice number a Code 128 symbol carries and the
    ''' "CS|I|…" payload of a QR tag, since both are printed on our documents.
    Private Sub ScanReceipt_KeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode <> Keys.Enter Then Return
        e.Handled = True
        e.SuppressKeyPress = True

        Dim scan = Barcodes.ReadScan(txtScanReceipt.Text)
        txtScanReceipt.Clear()
        If scan Is Nothing Then Return

        Dim match = DataAccess.GetTable(
            "SELECT TOP 1 InvoiceID FROM Invoices WHERE InvoiceNumber = @n",
            New Dictionary(Of String, Object) From {{"@n", scan.Code}})
        If match.Rows.Count = 0 Then
            AppUI.Toast($"No sale on file for {scan.Code}.", AppUI.ToastKind.Warning)
            Return
        End If

        Using f As New frmInvoiceReceipt(Convert.ToInt32(match.Rows(0)("InvoiceID")))
            f.ShowDialog(Me)
        End Using
    End Sub

    ''' Opens the receipt review (print preview + plain-English money breakdown)
    ''' for the selected invoice. "Save as PDF" from that window's toolbar.
    Private Sub OpenReceipt()
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Dim cell = grid.Grid.SelectedRows(0).Cells("InvoiceID").Value
        If cell Is Nothing OrElse cell Is DBNull.Value Then Return
        Using f As New frmInvoiceReceipt(Convert.ToInt32(cell))
            f.ShowDialog(Me)
        End Using
    End Sub

    Private Sub LoadGrid()
        Dim profitCol = If(isAdmin, ", (i.TotalAmount / (1 + i.VATRate/100.0)) - ISNULL((SELECT SUM(ii.Quantity * ii.UnitCost) FROM InvoiceItems ii WHERE ii.InvoiceID = i.InvoiceID), 0) AS EstProfit", "")
        Dim sql = "SELECT i.InvoiceNumber, c.Name AS Customer, i.InvoiceDate, " &
                  "i.PaymentMethod, i.TotalAmount, i.Status" & profitCol & ", i.InvoiceID " &
                  "FROM Invoices i JOIN Customers c ON c.CustomerID = i.CustomerID " &
                  "WHERE (@s IS NULL OR i.InvoiceNumber LIKE @s OR c.Name LIKE @s OR c.Phone LIKE @s OR i.PaymentMethod LIKE @s OR i.Status LIKE @s) " &
                  "ORDER BY i.InvoiceID DESC"
        Dim term = txtSearch.Text.Trim()
        Dim p As New Dictionary(Of String, Object) From {{"@s", If(term = "", CObj(DBNull.Value), "%" & term & "%")}}
        grid.Bind(DataAccess.GetTable(sql, p), hiddenColumns:={"InvoiceID"})
    End Sub

    Private Sub btnNewInvoice_Click(sender As Object, e As EventArgs)
        Using f As New frmNewInvoice(currentUserId)
            If f.ShowDialog() = DialogResult.OK Then LoadGrid()
        End Using
    End Sub

    ''' Wire to a context-menu/button per row (grid.CellClick) for rows where
    ''' Status <> 'Paid'. Records the payment and logs a Ledger credit entry.
    Private Sub MarkInvoicePaid(invoiceId As Integer, customerName As String, amount As Decimal, wasOnCredit As Boolean)
        Dim statements As New List(Of (Sql As String, Params As Dictionary(Of String, Object))) From {
            ("UPDATE Invoices SET Status = 'Paid' WHERE InvoiceID = @id", New Dictionary(Of String, Object) From {{"@id", invoiceId}}),
            ("INSERT INTO Payments (InvoiceID, Amount, Method, ReceivedByUserID) VALUES (@id, @amount, 'Cash', @userId)",
             New Dictionary(Of String, Object) From {{"@id", invoiceId}, {"@amount", amount}, {"@userId", currentUserId}})
        }
        If wasOnCredit Then
            statements.Add(("INSERT INTO Ledger (AccountType, AccountName, EntryType, Amount, Reference) VALUES ('Customer', @name, 'Credit', @amount, 'Payment')",
                New Dictionary(Of String, Object) From {{"@name", customerName}, {"@amount", amount}}))
        End If
        DataAccess.ExecuteTransaction(statements)
        LoadGrid()
    End Sub

End Class
