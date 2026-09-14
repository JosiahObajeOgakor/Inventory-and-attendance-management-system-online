Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data
Imports System.IO
Imports ClosedXML.Excel

''' Save / share a chosen month or year as PDF or Excel. The report bundles
''' sales, income, expenses, rebates and payroll for the period into one file.
''' Used from the Income screen and anywhere a "period report" button appears.
Public Module Exporter

    ''' Ask for a period + format, then produce the file.
    Public Sub RunPeriodReport(owner As IWin32Window, Optional presetYear As Integer? = Nothing, Optional presetMonth As Integer? = Nothing)
        Using dlg As New PeriodDialog(presetYear, presetMonth)
            If dlg.ShowDialog(owner) <> DialogResult.OK Then Return
            Dim sheets = BuildReport(dlg.SelectedYear, dlg.SelectedMonth)
            Dim label = AppInfo.PeriodLabel(dlg.SelectedYear, dlg.SelectedMonth)
            Dim baseName = Company.Current.FilePrefix & "_report_" &
                If(dlg.SelectedMonth.HasValue, $"{dlg.SelectedYear:0000}-{dlg.SelectedMonth.Value:00}", $"{dlg.SelectedYear:0000}")

            If dlg.AsExcel Then
                SaveExcel(sheets, baseName, owner)
            Else
                PreviewPdf(owner, Company.Current.DisplayName & " — Report — " & label, label, sheets)
            End If
        End Using
    End Sub

    ''' Assemble the period's data tables (also reused as Excel sheets).
    Public Function BuildReport(year As Integer, month As Integer?) As Dictionary(Of String, DataTable)
        Dim p As New Dictionary(Of String, Object) From {{"@y", year}, {"@m", If(month.HasValue, CObj(month.Value), DBNull.Value)}}
        Dim monthFilter = "YEAR(#COL#) = @y AND (@m IS NULL OR MONTH(#COL#) = @m)"

        Dim sheets As New Dictionary(Of String, DataTable)

        sheets("Summary") = DataAccess.GetTable(
            "SELECT " &
            " (SELECT ISNULL(SUM(TotalAmount),0) FROM Invoices WHERE " & monthFilter.Replace("#COL#", "InvoiceDate") & ") AS GrossSales, " &
            " (SELECT ISNULL(SUM(VATAmount),0)   FROM Invoices WHERE " & monthFilter.Replace("#COL#", "InvoiceDate") & ") AS VAT, " &
            " (SELECT ISNULL(SUM(AmountPaid),0)  FROM Invoices WHERE " & monthFilter.Replace("#COL#", "InvoiceDate") & ") AS Collected, " &
            " (SELECT ISNULL(SUM(TotalAmount - AmountPaid),0) FROM Invoices WHERE " & monthFilter.Replace("#COL#", "InvoiceDate") & ") AS Outstanding, " &
            " (SELECT ISNULL(SUM(ii.Quantity*ii.UnitCost),0) FROM InvoiceItems ii JOIN Invoices i ON i.InvoiceID=ii.InvoiceID WHERE " & monthFilter.Replace("#COL#", "i.InvoiceDate") & ") AS COGS, " &
            " (SELECT ISNULL(SUM(Amount),0) FROM Expenses WHERE " & monthFilter.Replace("#COL#", "ExpenseDate") & ") AS Expenses, " &
            " (SELECT ISNULL(SUM(Amount),0) FROM RebateEntries WHERE [Status]='Accrued' AND " & monthFilter.Replace("#COL#", "EntryDate") & ") AS RebateAccrued", p)

        sheets("Sales") = DataAccess.GetTable(
            "SELECT i.InvoiceNumber, i.InvoiceDate, c.Name AS Customer, c.CustomerType, i.PriceTier, " &
            "i.PaymentMethod, i.[Status], i.TotalAmount, i.AmountPaid, (i.TotalAmount - i.AmountPaid) AS Balance, i.DueDate " &
            "FROM Invoices i JOIN Customers c ON c.CustomerID = i.CustomerID " &
            "WHERE " & monthFilter.Replace("#COL#", "i.InvoiceDate") & " ORDER BY i.InvoiceDate, i.InvoiceNumber", p)

        sheets("Expenses") = DataAccess.GetTable(
            "SELECT ExpenseDate, Category, Amount, Note FROM Expenses " &
            "WHERE " & monthFilter.Replace("#COL#", "ExpenseDate") & " ORDER BY ExpenseDate", p)

        sheets("Rebates") = DataAccess.GetTable(
            "SELECT r.EntryDate, c.Name AS Customer, r.Amount, r.[Status], r.RedeemedDate " &
            "FROM RebateEntries r JOIN Customers c ON c.CustomerID = r.CustomerID " &
            "WHERE " & monthFilter.Replace("#COL#", "r.EntryDate") & " ORDER BY r.EntryDate", p)

        sheets("Payroll") = DataAccess.GetTable(
            "SELECT em.PeriodYear, em.PeriodMonth, e.FullName, em.Position, em.SalaryAmount, em.LoanDeduction, " &
            "(em.SalaryAmount - em.LoanDeduction) AS NetPay, em.Paid, em.PaidDate " &
            "FROM EmployeeMonthly em JOIN Employees e ON e.EmployeeID = em.EmployeeID " &
            "WHERE em.PeriodYear = @y AND (@m IS NULL OR em.PeriodMonth = @m) " &
            "ORDER BY em.PeriodMonth, e.FullName", p)

        Return sheets
    End Function

    ''' Every business table as one workbook — Users/LoginAudit are deliberately
    ''' excluded (credential data belongs in the .bak backup, not a spreadsheet
    ''' that gets emailed around).
    Public Function BuildFullExport() As Dictionary(Of String, DataTable)
        Dim tables = {
            "Categories", "Warehouses", "Products", "StockBatches", "StockMovements",
            "Suppliers", "PurchaseOrders", "PurchaseOrderItems", "Customers",
            "Invoices", "InvoiceItems", "Expenses", "Payments", "Ledger",
            "RebateEntries", "Employees", "EmployeeMonthly", "EmployeeLoans",
            "LoanRepayments", "Waybills"
        }
        Dim sheets As New Dictionary(Of String, DataTable)
        For Each tableName In tables
            sheets(tableName) = DataAccess.GetTable("SELECT * FROM " & tableName)
        Next
        Return sheets
    End Function

    ' ===== Excel =====

    Public Sub SaveExcel(sheets As IDictionary(Of String, DataTable), baseName As String, owner As IWin32Window)
        Using sfd As New SaveFileDialog() With {.Filter = "Excel workbook (*.xlsx)|*.xlsx", .FileName = baseName & ".xlsx"}
            If sfd.ShowDialog(owner) <> DialogResult.OK Then Return
            Try
                Using wb As New XLWorkbook()
                    For Each kv In sheets
                        Dim ws = wb.Worksheets.Add(SafeSheetName(kv.Key))
                        If kv.Value Is Nothing OrElse kv.Value.Columns.Count = 0 Then
                            ws.Cell(1, 1).Value = "(no data)"
                        ElseIf kv.Value.Rows.Count = 0 Then
                            For c = 0 To kv.Value.Columns.Count - 1
                                ws.Cell(1, c + 1).Value = kv.Value.Columns(c).ColumnName
                                ws.Cell(1, c + 1).Style.Font.Bold = True
                            Next
                            ws.Cell(2, 1).Value = "(no records for this period)"
                        Else
                            ws.Cell(1, 1).InsertTable(kv.Value)
                        End If
                        ws.Columns().AdjustToContents()
                    Next
                    wb.SaveAs(sfd.FileName)
                End Using
                AppUI.Toast("Saved " & Path.GetFileName(sfd.FileName), AppUI.ToastKind.Success)
            Catch ex As Exception
                AppUI.Toast("Excel export failed: " & ex.Message, AppUI.ToastKind.Error)
            End Try
        End Using
    End Sub

    ''' One-grid convenience used by screen "Export Excel" buttons.
    Public Sub SaveExcel(single_ As DataTable, sheetName As String, baseName As String, owner As IWin32Window)
        SaveExcel(New Dictionary(Of String, DataTable) From {{sheetName, single_}}, baseName, owner)
    End Sub

    Private Function SafeSheetName(name As String) As String
        For Each ch In "[]:*?/\".ToCharArray()
            name = name.Replace(ch, " "c)
        Next
        Return If(name.Length > 31, name.Substring(0, 31), name)
    End Function

    ' ===== PDF (via DocPrinter → print preview → Save as PDF) =====

    Public Sub PreviewPdf(owner As IWin32Window, docTitle As String, periodLabel As String, sheets As IDictionary(Of String, DataTable))
        ' Wide tables (e.g. Sales has 11 columns) get landscape pages so nothing is cut off.
        Dim widest = sheets.Values.Where(Function(t) t IsNot Nothing).Select(Function(t) t.Columns.Count).DefaultIfEmpty(0).Max()
        Dim d As New DocPrinter() With {.DocTitle = docTitle, .Landscape = widest > 7}
        d.FooterText = $"{AppInfo.CompanyName}   ·   Business report — {periodLabel}"
        d.Letterhead("BUSINESS REPORT", {("Period", periodLabel), ("Generated", DateTime.Now.ToString("dd MMM yyyy HH:mm"))})

        If sheets.ContainsKey("Summary") AndAlso sheets("Summary").Rows.Count > 0 Then
            Dim s = sheets("Summary").Rows(0)
            Dim gross = Convert.ToDecimal(s("GrossSales"))
            Dim vat = Convert.ToDecimal(s("VAT"))
            Dim cogs = Convert.ToDecimal(s("COGS"))
            Dim exp = Convert.ToDecimal(s("Expenses"))
            Dim net = (gross - vat) - cogs - exp
            d.SectionTitle("Summary")
            Dim summary As New DataTable()
            summary.Columns.Add("Measure")
            summary.Columns.Add($"Amount ({AppInfo.CurrencySymbol})")
            For Each row In {
                ("Gross sales (incl. VAT)", gross), ("VAT collected", vat), ("Net sales (excl. VAT)", gross - vat),
                ("Cost of goods sold", cogs), ("Operating expenses", exp),
                ("Cash collected", Convert.ToDecimal(s("Collected"))), ("Still outstanding", Convert.ToDecimal(s("Outstanding"))),
                ("Rebate accrued this period", Convert.ToDecimal(s("RebateAccrued")))}
                summary.Rows.Add(row.Item1, row.Item2.ToString("N2"))
            Next
            d.Table(summary, {3.0F, 1.2F}, rightAlignFrom:=1, footer:={"Estimated net profit", net.ToString("N2")})
            d.Gap(6)
        End If

        For Each name In {"Sales", "Expenses", "Rebates", "Payroll"}
            If Not sheets.ContainsKey(name) Then Continue For
            d.SectionTitle(name)
            If sheets(name).Rows.Count = 0 Then
                d.Text("(nothing recorded for this period)", grey:=True)
            Else
                d.Table(sheets(name), fontSize:=8.5F)
            End If
            d.Gap(6)
        Next

        d.ShowPreview(owner)
    End Sub

    ' ===== period + format picker =====

    Private NotInheritable Class PeriodDialog
        Inherits Form
        Private ReadOnly cboYear As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 120}
        Private ReadOnly cboMonth As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 160}
        Private ReadOnly rbPdf As New RadioButton() With {.Text = "PDF", .Checked = True, .AutoSize = True}
        Private ReadOnly rbXlsx As New RadioButton() With {.Text = "Excel (.xlsx)", .AutoSize = True}

        Public ReadOnly Property SelectedYear As Integer
            Get
                Return CInt(cboYear.SelectedItem)
            End Get
        End Property
        Public ReadOnly Property SelectedMonth As Integer?
            Get
                Return If(cboMonth.SelectedIndex <= 0, CType(Nothing, Integer?), cboMonth.SelectedIndex)
            End Get
        End Property
        Public ReadOnly Property AsExcel As Boolean
            Get
                Return rbXlsx.Checked
            End Get
        End Property

        Public Sub New(presetYear As Integer?, presetMonth As Integer?)
            Text = "Save / share report"
            FormBorderStyle = FormBorderStyle.FixedDialog
            StartPosition = FormStartPosition.CenterParent
            MinimizeBox = False : MaximizeBox = False
            ClientSize = New Size(360, 210)

            Dim thisYear = DateTime.Now.Year
            For y = thisYear + 1 To thisYear - 6 Step -1
                cboYear.Items.Add(y)
            Next
            cboYear.SelectedItem = If(presetYear.HasValue, presetYear.Value, thisYear)

            cboMonth.Items.Add("Whole year")
            For m = 1 To 12
                cboMonth.Items.Add(AppInfo.MonthName(m))
            Next
            cboMonth.SelectedIndex = If(presetMonth.HasValue, presetMonth.Value, DateTime.Now.Month)

            Dim t = UiHelpers.NewFormTable()
            UiHelpers.AddLabeled(t, "Year", cboYear)
            UiHelpers.AddLabeled(t, "Month", cboMonth)
            Dim fmt As New FlowLayoutPanel() With {.AutoSize = True}
            fmt.Controls.Add(rbPdf)
            fmt.Controls.Add(rbXlsx)
            UiHelpers.AddLabeled(t, "Format", fmt)

            Controls.Add(t)
            UiHelpers.AddOkCancelRow(Me, "Create", Sub(s, e)
                                                        DialogResult = DialogResult.OK
                                                        Close()
                                                    End Sub)
            Theme.Apply(Me)
        End Sub
    End Class

End Module
