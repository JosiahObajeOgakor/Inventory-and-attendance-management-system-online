Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data

''' Rebates. Every credit-customer sale accrues a % (default 1%, per-customer
''' overridable) as rebate. This screen shows each customer's rebate NOT YET
''' COLLECTED and what's already been REDEEMED, and lets you redeem a balance
''' (given back as goods). The rate is set here and stored in AppSettings.
Public Class ucRebates
    Inherits UserControl

    Private ReadOnly grid As New PagedGrid() With {.PageSize = 5}
    Private ReadOnly gridDetail As New PagedGrid() With {.PageSize = 5}
    Private numRate As New NumericUpDown() With {.Maximum = 100, .DecimalPlaces = 2, .Width = 70}
    Private btnSaveRate As New Button() With {.Text = "Save default rate", .AutoSize = True}
    Private btnRedeem As New Button() With {.Text = "Redeem selected customer's rebate", .Tag = "primary", .AutoSize = True, .Enabled = False}
    Private btnExport As New Button() With {.Text = "Export CSV", .AutoSize = True}
    Private lblTotals As New Label() With {.AutoSize = True, .Tag = "keepfont", .Font = New Font("Segoe UI", 11, FontStyle.Bold)}
    Private txtSearch As New TextBox() With {.Width = 180}

    Public Sub New()
        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(12)}
        bar.Controls.Add(New Label() With {.Text = "Default rebate rate %:", .AutoSize = True, .Margin = New Padding(0, 8, 6, 0)})
        bar.Controls.Add(numRate)
        bar.Controls.Add(btnSaveRate)
        bar.Controls.Add(btnRedeem)
        bar.Controls.Add(btnExport)
        bar.Controls.Add(New Label() With {.Text = "Search:", .AutoSize = True, .Margin = New Padding(12, 8, 6, 0)})
        bar.Controls.Add(txtSearch)
        bar.Controls.Add(lblTotals)

        Dim split As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .RowCount = 2}
        split.RowStyles.Add(New RowStyle(SizeType.Percent, 55))
        split.RowStyles.Add(New RowStyle(SizeType.Percent, 45))

        Dim topPanel As New Panel() With {.Dock = DockStyle.Fill}
        topPanel.Controls.Add(grid)
        topPanel.Controls.Add(New Label() With {.Text = "Rebate by customer", .Dock = DockStyle.Top, .Tag = "heading", .AutoSize = True, .Padding = New Padding(8)})

        Dim botPanel As New Panel() With {.Dock = DockStyle.Fill}
        botPanel.Controls.Add(gridDetail)
        botPanel.Controls.Add(New Label() With {.Text = "Rebate entries for selected customer", .Dock = DockStyle.Top, .Tag = "heading", .AutoSize = True, .Padding = New Padding(8)})

        split.Controls.Add(topPanel, 0, 0)
        split.Controls.Add(botPanel, 0, 1)

        Controls.Add(split)
        Controls.Add(bar)

        AddHandler btnSaveRate.Click, AddressOf SaveRate_Click
        AddHandler btnRedeem.Click, AddressOf Redeem_Click
        AddHandler btnExport.Click, Sub(s, e) AppUI.ExportCsv(grid.AllRows(), "rebates", FindForm())
        AddHandler txtSearch.TextChanged, Sub(s, e) LoadGrid()
        AddHandler grid.Grid.SelectionChanged, Sub(s, e)
                                              btnRedeem.Enabled = grid.Grid.SelectedRows.Count > 0 AndAlso RebateAvailable() > 0
                                              LoadDetail()
                                          End Sub
        AddHandler Me.Load, Sub(s, e)
                                numRate.Value = AppInfo.RebateRatePct
                                LoadGrid()
                            End Sub
    End Sub

    Private Function RebateAvailable() As Decimal
        If grid.Grid.SelectedRows.Count = 0 Then Return 0
        Return Convert.ToDecimal(grid.Grid.SelectedRows(0).Cells("Not yet collected").Value)
    End Function

    Private Sub LoadGrid()
        Dim term = txtSearch.Text.Trim()
        Dim p As New Dictionary(Of String, Object) From {{"@s", If(term = "", CObj(DBNull.Value), "%" & term & "%")}}
        ' The friendlier headings are aliases in the query rather than tweaks
        ' applied after binding — a tweak would be lost the moment the table
        ' turned a page and rebuilt its columns.
        grid.Bind(DataAccess.GetTable(
            "SELECT CustomerID, Customer, CustomerType AS Ranking, " &
            "RebateAvailable AS [Not yet collected], RebateRedeemed AS [Already received], RebateLifetime AS [Lifetime] " &
            "FROM vw_CustomerRebate WHERE RebateLifetime > 0 AND (@s IS NULL OR Customer LIKE @s OR CustomerType LIKE @s) " &
            "ORDER BY RebateAvailable DESC", p), hiddenColumns:={"CustomerID"})

        Dim t = DataAccess.GetTable("SELECT ISNULL(SUM(RebateAvailable),0) AS A, ISNULL(SUM(RebateRedeemed),0) AS R FROM vw_CustomerRebate").Rows(0)
        lblTotals.Text = $"Outstanding rebate: {AppInfo.Money(t("A"))}    ·    Redeemed to date: {AppInfo.Money(t("R"))}"
    End Sub

    Private Sub LoadDetail()
        If grid.Grid.SelectedRows.Count = 0 Then
            gridDetail.Bind(New DataTable())
            Return
        End If
        Dim cid = CInt(grid.Grid.SelectedRows(0).Cells("CustomerID").Value)
        gridDetail.Bind(DataAccess.GetTable(
            "SELECT r.EntryDate, i.InvoiceNumber, r.Amount, r.[Status], r.RedeemedDate, r.Note " &
            "FROM RebateEntries r LEFT JOIN Invoices i ON i.InvoiceID=r.InvoiceID " &
            "WHERE r.CustomerID=@c ORDER BY r.RebateEntryID DESC",
            New Dictionary(Of String, Object) From {{"@c", cid}}))
    End Sub

    Private Sub SaveRate_Click(sender As Object, e As EventArgs)
        DataAccess.Execute(
            "MERGE AppSettings AS t USING (SELECT 'rebate.ratePct' AS k) s ON t.SettingKey=s.k " &
            "WHEN MATCHED THEN UPDATE SET SettingValue=@v WHEN NOT MATCHED THEN INSERT (SettingKey,SettingValue) VALUES ('rebate.ratePct',@v);",
            New Dictionary(Of String, Object) From {{"@v", numRate.Value.ToString()}})
        AppUI.Toast($"Default rebate rate set to {numRate.Value}%.", AppUI.ToastKind.Success)
    End Sub

    Private Sub Redeem_Click(sender As Object, e As EventArgs)
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Dim cid = CInt(grid.Grid.SelectedRows(0).Cells("CustomerID").Value)
        Dim name = Convert.ToString(grid.Grid.SelectedRows(0).Cells("Customer").Value)
        Dim available = RebateAvailable()
        If available <= 0 Then Return
        If Not AppUI.Confirm(FindForm(),
            $"Redeem {name}'s outstanding rebate of {AppInfo.Money(available)}?" & vbCrLf & vbCrLf &
            "This marks the rebate as given back (as goods). Record the goods issued separately as a sale at " &
            AppInfo.Money(0) & " or a stock adjustment.", "Redeem rebate", "Redeem") Then Return

        DataAccess.Execute(
            "UPDATE RebateEntries SET [Status]='Redeemed', RedeemedDate=CAST(SYSDATETIME() AS DATE) WHERE CustomerID=@c AND [Status]='Accrued'",
            New Dictionary(Of String, Object) From {{"@c", cid}})
        AppUI.Toast($"{AppInfo.Money(available)} rebate redeemed for {name}.", AppUI.ToastKind.Success)
        LoadGrid()
        LoadDetail()
    End Sub

End Class
