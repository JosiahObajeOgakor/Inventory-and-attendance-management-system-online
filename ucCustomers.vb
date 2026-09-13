Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data

''' Customers: details + ranking (Distributor / Wholesaler / Retailer / Walk-in),
''' Tax ID, 12-month spend / tier, rebate balance, add / edit / delete, record
''' payment, view metrics. Admin-only screen.
Public Class ucCustomers
    Inherits UserControl

    Private ReadOnly currentUserId As Integer
    Private ReadOnly grid As New PagedGrid() With {.PageSize = 5}
    Private txtSearch As New TextBox() With {.Width = 200}
    Private lblTotals As New Label() With {.AutoSize = True, .Tag = "keepfont", .Font = New Font("Segoe UI", 11, FontStyle.Bold)}
    Private btnAdd As New Button() With {.Text = "+ Add customer", .Tag = "primary", .AutoSize = True}
    Private btnEdit As New Button() With {.Text = "Edit", .AutoSize = True, .Enabled = False}
    Private btnRecordPayment As New Button() With {.Text = "Record payment", .AutoSize = True, .Enabled = False}
    Private btnViewMetrics As New Button() With {.Text = "View metrics", .AutoSize = True, .Enabled = False}
    Private btnExport As New Button() With {.Text = "Export CSV", .AutoSize = True}
    Private btnExportXlsx As New Button() With {.Text = "Export Excel", .AutoSize = True}
    Private btnDelete As New Button() With {.Text = "Delete", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private toolbar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(12)}

    Public Sub New(userId As Integer)
        currentUserId = userId
        toolbar.Controls.Add(lblTotals)
        toolbar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
        toolbar.Controls.Add(txtSearch)
        toolbar.Controls.Add(btnAdd)
        toolbar.Controls.Add(btnEdit)
        toolbar.Controls.Add(btnRecordPayment)
        toolbar.Controls.Add(btnViewMetrics)
        toolbar.Controls.Add(btnExport)
        toolbar.Controls.Add(btnExportXlsx)
        toolbar.Controls.Add(btnDelete)
        Controls.Add(grid)
        Controls.Add(toolbar)

        AddHandler grid.Grid.SelectionChanged, Sub(s, e)
                                              Dim has = grid.Grid.SelectedRows.Count > 0
                                              btnEdit.Enabled = has
                                              btnRecordPayment.Enabled = has
                                              btnViewMetrics.Enabled = has
                                              btnDelete.Enabled = has
                                          End Sub
        AddHandler grid.Grid.CellDoubleClick, Sub(s, e) EditSelected()
        AddHandler btnAdd.Click, AddressOf btnAdd_Click
        AddHandler btnEdit.Click, Sub(s, e) EditSelected()
        AddHandler btnRecordPayment.Click, AddressOf btnRecordPayment_Click
        AddHandler btnViewMetrics.Click, AddressOf btnViewMetrics_Click
        AddHandler btnExport.Click, Sub(s, e) AppUI.ExportCsv(grid.AllRows(), "customers", FindForm())
        AddHandler btnExportXlsx.Click, Sub(s, e) Exporter.SaveExcel(grid.AllRows(), "Customers", "ChewyPetsFeed_customers", FindForm())
        AddHandler btnDelete.Click, AddressOf btnDelete_Click
        AddHandler txtSearch.TextChanged, Sub(s, e) LoadGrid()
        AddHandler Me.Load, Sub(s, e) LoadGrid()
    End Sub

    Private Sub LoadGrid()
        Dim sql =
            "SELECT c.CustomerID, c.Name, c.CustomerType AS Ranking, c.Phone, c.TaxID, c.Location, " &
            "c.CreditLimit, c.Balance AS OwingUs, " &
            "ISNULL(cr.RebateAvailable,0) AS RebateAvailable, ISNULL(cr.RebateRedeemed,0) AS RebateRedeemed, " &
            "ISNULL((SELECT SUM(i.TotalAmount) FROM Invoices i WHERE i.CustomerID=c.CustomerID AND i.InvoiceDate>=DATEADD(MONTH,-12,GETDATE())),0) AS Spend12mo " &
            "FROM Customers c LEFT JOIN vw_CustomerRebate cr ON cr.CustomerID = c.CustomerID " &
            "WHERE (@s IS NULL OR c.Name LIKE @s OR c.Phone LIKE @s OR c.Email LIKE @s OR c.Location LIKE @s OR c.CustomerType LIKE @s OR c.TaxID LIKE @s) " &
            "ORDER BY Spend12mo DESC"
        Dim term = txtSearch.Text.Trim()
        Dim p As New Dictionary(Of String, Object) From {{"@s", If(term = "", CObj(DBNull.Value), "%" & term & "%")}}
        Dim table = DataAccess.GetTable(sql, p)
        table.Columns.Add("Tier", GetType(String))
        For Each r As DataRow In table.Rows
            Dim total = Convert.ToDecimal(r("Spend12mo"))
            r("Tier") = If(total >= 800000, "Gold", If(total >= 400000, "Silver", "Bronze"))
        Next
        grid.Bind(table, hiddenColumns:={"CustomerID"})

        Dim ar = DataAccess.GetTable("SELECT ISNULL(SUM(Balance),0) AS Total FROM Customers").Rows(0)("Total")
        Dim reb = DataAccess.GetTable("SELECT ISNULL(SUM(RebateAvailable),0) AS Total FROM vw_CustomerRebate").Rows(0)("Total")
        lblTotals.Text = $"Owed to us: {AppInfo.Money(ar)}    ·    Rebate outstanding: {AppInfo.Money(reb)}"
    End Sub

    Private Function SelectedCustomer() As (Id As Integer, Name As String, Balance As Decimal)
        Dim row = grid.Grid.SelectedRows(0)
        Return (CInt(row.Cells("CustomerID").Value), Convert.ToString(row.Cells("Name").Value), Convert.ToDecimal(row.Cells("OwingUs").Value))
    End Function

    Private Sub btnAdd_Click(sender As Object, e As EventArgs)
        Using f As New frmAddCustomer()
            If f.ShowDialog() = DialogResult.OK Then
                DataAccess.Execute(
                    "INSERT INTO Customers (Name, CustomerType, ContactName, Phone, Email, [Address], Location, TaxID, RebateRatePct, CreditLimit, Balance) " &
                    "VALUES (@n, @t, @c, @p, @e, @a, @l, @x, @rb, @cl, 0)", f.Params())
                AppUI.Toast($"Added {f.CustomerName}.", AppUI.ToastKind.Success)
                LoadGrid()
            End If
        End Using
    End Sub

    Private Sub EditSelected()
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Using f As New frmAddCustomer(CInt(grid.Grid.SelectedRows(0).Cells("CustomerID").Value))
            If f.ShowDialog() = DialogResult.OK Then
                AppUI.Toast("Customer updated.", AppUI.ToastKind.Success)
                LoadGrid()
            End If
        End Using
    End Sub

    Private Sub btnDelete_Click(sender As Object, e As EventArgs)
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Dim c = SelectedCustomer()
        If c.Balance <> 0 Then
            AppUI.Info(FindForm(), $"{c.Name} still owes {AppInfo.Money(c.Balance)}. Settle it before deleting.")
            Return
        End If
        If AppUI.TryDelete(FindForm(), $"customer ""{c.Name}""",
                           "DELETE FROM Customers WHERE CustomerID = @id",
                           New Dictionary(Of String, Object) From {{"@id", c.Id}}) Then
            LoadGrid()
        End If
    End Sub

    Private Sub btnRecordPayment_Click(sender As Object, e As EventArgs)
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Dim c = SelectedCustomer()
        If c.Balance <= 0 Then
            AppUI.Info(FindForm(), "This customer has no outstanding balance.")
            Return
        End If
        Using f As New frmRecordPayment(c.Name, c.Balance)
            If f.ShowDialog() = DialogResult.OK Then
                DataAccess.ExecuteTransaction(New List(Of (Sql As String, Params As Dictionary(Of String, Object))) From {
                    ("UPDATE Customers SET Balance = Balance - @amount WHERE CustomerID = @id",
                     New Dictionary(Of String, Object) From {{"@amount", f.Amount}, {"@id", c.Id}}),
                    ("UPDATE Invoices SET AmountPaid = AmountPaid + @amount, " &
                     "[Status] = CASE WHEN AmountPaid + @amount >= TotalAmount THEN 'Paid' ELSE 'Partial' END " &
                     "WHERE InvoiceID = (SELECT TOP 1 InvoiceID FROM Invoices WHERE CustomerID=@id AND [Status]<>'Paid' ORDER BY InvoiceDate)",
                     New Dictionary(Of String, Object) From {{"@amount", f.Amount}, {"@id", c.Id}}),
                    ("INSERT INTO Ledger (AccountType, AccountName, EntryType, Amount, Reference) VALUES ('Customer', @name, 'Credit', @amount, 'Payment')",
                     New Dictionary(Of String, Object) From {{"@name", c.Name}, {"@amount", f.Amount}})
                })
                AppUI.Toast($"Payment of {AppInfo.Money(f.Amount)} recorded.", AppUI.ToastKind.Success)
                LoadGrid()
            End If
        End Using
    End Sub

    Private Sub btnViewMetrics_Click(sender As Object, e As EventArgs)
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Dim c = SelectedCustomer()
        Using f As New frmCustomerMetrics(c.Id, c.Name)
            f.ShowDialog()
        End Using
    End Sub

End Class
