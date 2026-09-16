Imports System.Windows.Forms
Imports System.Drawing
Imports System.Data

''' Receipt / invoice for one sale. Left tab: a designed, printable receipt with
''' the company logo, address & contact, bank details in bold, invoice number,
''' the customer's name / address / Tax ID, line items, amount paid, balance
''' still owed, expected pay-off date, any balance owed from earlier purchases,
''' the customer's rebate balance and a thank-you. Right tab: a plain-English
''' breakdown of the money. "Save as PDF" from the receipt view's print dialog.
Public Class frmInvoiceReceipt
    Inherits Form

    Private ReadOnly _header As DataRow
    Private ReadOnly _items As DataTable
    Private ReadOnly _rebate As Decimal
    ''' What this customer owes right now, across every invoice — always ≥ this
    ''' invoice's own balance due, since that's one component of it.
    Private ReadOnly _customerBalance As Decimal

    Public Sub New(invoiceId As Integer)
        _header = DataAccess.GetTable(
            "SELECT i.InvoiceNumber, i.InvoiceDate, i.DueDate, i.Subtotal, i.DiscountPct, i.DiscountAmount, " &
            "i.VATRate, i.VATAmount, i.TotalAmount, i.AmountPaid, i.PaymentMethod, i.[Status], i.PriceTier, " &
            "ISNULL(w.Name,'') AS Warehouse, " &
            "c.CustomerID, c.Name AS CustomerName, c.ContactName, c.Phone AS CustomerPhone, c.Email AS CustomerEmail, " &
            "ISNULL(c.[Address],'') AS CustomerAddress, ISNULL(c.Location,'') AS CustomerLocation, ISNULL(c.TaxID,'') AS CustomerTaxID, " &
            "c.CustomerType, ISNULL(u.FullName,'') AS CreatedBy, c.Balance AS CustomerBalance, " &
            "ISNULL((SELECT SUM(ii.Quantity*ii.UnitCost) FROM InvoiceItems ii WHERE ii.InvoiceID=i.InvoiceID),0) AS TotalCost " &
            "FROM Invoices i JOIN Customers c ON c.CustomerID=i.CustomerID " &
            "LEFT JOIN Warehouses w ON w.WarehouseID=i.WarehouseID " &
            "LEFT JOIN Users u ON u.UserID=i.CreatedByUserID WHERE i.InvoiceID=@id",
            New Dictionary(Of String, Object) From {{"@id", invoiceId}}).Rows(0)

        _items = DataAccess.GetTable(
            "SELECT p.Name AS Item, ii.Quantity AS Qty, ii.UnitPrice AS Price, ii.LineTotal AS [Line total] " &
            "FROM InvoiceItems ii JOIN Products p ON p.ProductID=ii.ProductID WHERE ii.InvoiceID=@id ORDER BY p.Name",
            New Dictionary(Of String, Object) From {{"@id", invoiceId}})

        _rebate = Convert.ToDecimal(DataAccess.GetTable(
            "SELECT ISNULL(RebateAvailable,0) AS R FROM vw_CustomerRebate WHERE CustomerID=@c",
            New Dictionary(Of String, Object) From {{"@c", _header("CustomerID")}}).Rows(0)("R"))

        _customerBalance = Convert.ToDecimal(_header("CustomerBalance"))

        Text = AppInfo.CompanyName & " — Receipt " & Convert.ToString(_header("InvoiceNumber"))
        Width = 1000
        Height = 900
        StartPosition = FormStartPosition.CenterParent
        UiHelpers.FitToScreen(Me)

        Dim tabs As New TabControl() With {.Dock = DockStyle.Fill}
        tabs.TabPages.Add(BuildReceiptTab())
        tabs.TabPages.Add(BuildBreakdownTab())

        Controls.Add(tabs)
        Theme.Apply(Me)
    End Sub

    Private Function Bal() As Decimal
        Return Convert.ToDecimal(_header("TotalAmount")) - Convert.ToDecimal(_header("AmountPaid"))
    End Function

    ''' Owed from OTHER invoices — the customer's total balance minus this
    ''' invoice's own share of it. Clamped at zero against any rounding drift.
    Private Function OtherBalance() As Decimal
        Return Math.Max(0D, _customerBalance - Bal())
    End Function

    ' ===== Receipt document =====

    Private Function BuildReceiptDoc() As DocPrinter
        Dim number = Convert.ToString(_header("InvoiceNumber"))
        Dim s = Function(col As String) Convert.ToString(_header(col))
        ' Always exactly one A4 page — the whole receipt scales down to fit, nothing is cut.
        Dim d As New DocPrinter() With {
            .DocTitle = "Receipt " & number, .FitToOnePage = True,
            .FooterText = $"{AppInfo.CompanyName}   ·   Receipt {number}   ·   computer-generated {DateTime.Now:dd MMM yyyy HH:mm}"}

        d.Letterhead("SALES RECEIPT", {
            ("Invoice no.", number),
            ("Date", Convert.ToDateTime(_header("InvoiceDate")).ToString("dd MMM yyyy")),
            ("Status", s("Status").ToUpperInvariant())})

        Dim due = If(Bal() > 0 AndAlso _header("DueDate") IsNot DBNull.Value,
                     Convert.ToDateTime(_header("DueDate")).ToString("dd MMM yyyy"), "")
        d.Panels(
            ("Bill to", {
                ("Customer", s("CustomerName"), True),
                ("Contact", s("ContactName"), False),
                ("Address", OneLine(s("CustomerAddress"), s("CustomerLocation")), False),
                ("Phone", s("CustomerPhone"), False),
                ("Email", s("CustomerEmail"), False),
                ("Tax ID", s("CustomerTaxID"), False),
                ("Ranking", s("CustomerType"), False)}),
            ("Sale details", {
                ("Price tier", s("PriceTier"), False),
                ("Warehouse", s("Warehouse"), False),
                ("Payment", s("PaymentMethod"), False),
                ("Served by", s("CreatedBy"), False),
                ("Due date", due, True)}))

        ' Items — numbered, money right-aligned in fixed columns.
        Dim items As New DataTable()
        For Each c In {"#", "Description", "Qty", $"Unit price ({AppInfo.CurrencySymbol})", $"Amount ({AppInfo.CurrencySymbol})"}
            items.Columns.Add(c)
        Next
        Dim n = 0, units = 0
        For Each r As DataRow In _items.Rows
            n += 1
            units += Convert.ToInt32(r("Qty"))
            items.Rows.Add(n, r("Item"), Convert.ToInt32(r("Qty")).ToString("#,0"),
                           Convert.ToDecimal(r("Price")).ToString("N2"), Convert.ToDecimal(r("Line total")).ToString("N2"))
        Next
        d.Table(items, {0.45F, 4.4F, 0.9F, 1.6F, 1.7F}, rightAlignFrom:=2)

        ' Totals box, amounts aligned under the Amount column.
        Dim totals As New List(Of (K As String, V As String, Style As Integer)) From {
            ("Subtotal", AppInfo.Money2(_header("Subtotal")), 0)}
        If Convert.ToDecimal(_header("DiscountAmount")) > 0 Then
            totals.Add(($"Discount ({Convert.ToDecimal(_header("DiscountPct")):0.##}%)", "−" & AppInfo.Money2(_header("DiscountAmount")), 0))
        End If
        ' VAT only appears when it was actually charged on this sale.
        If Convert.ToDecimal(_header("VATAmount")) > 0 Then
            totals.Add(($"VAT ({Convert.ToDecimal(_header("VATRate")):0.##}%)", AppInfo.Money2(_header("VATAmount")), 0))
        End If
        totals.Add(("TOTAL", AppInfo.Money2(_header("TotalAmount")), 1))
        totals.Add(("Amount paid", AppInfo.Money2(_header("AmountPaid")), 0))
        totals.Add(("Balance due (this invoice)", AppInfo.Money2(Bal()), If(Bal() > 0, 2, 0)))
        ' Never buried — a customer's earlier debt is shown right alongside
        ' today's balance, with the combined figure spelled out underneath it.
        Dim owedElsewhere = OtherBalance()
        If owedElsewhere > 0 Then
            totals.Add(("Owed from earlier purchases", AppInfo.Money2(owedElsewhere), 2))
            totals.Add(("TOTAL NOW OWED (all invoices)", AppInfo.Money2(Bal() + owedElsewhere), 2))
        End If
        d.Totals(totals, {
            ($"{n} product line(s) · {units:#,0} unit(s)", False),
            ($"Paid by {s("PaymentMethod")} · status {s("Status")}", False),
            (If(due = "", "", $"Balance to be paid by {due}"), True),
            (If(owedElsewhere > 0,
                $"This customer also owes {AppInfo.Money2(owedElsewhere)} from earlier purchases — please collect the full amount owed where possible.",
                ""), True),
            ($"Rebate balance with us: {AppInfo.Money2(_rebate)} (redeemable as goods)", False)})

        Dim banks = AppInfo.BankAccounts()
        If banks.Count > 0 Then
            d.SectionTitle("Payment details — bank transfer")
            Dim bt As New DataTable()
            bt.Columns.Add("Bank")
            bt.Columns.Add("Account name")
            bt.Columns.Add("Account number")
            For Each b In banks
                bt.Rows.Add(b.Bank, b.AccountName, b.AccountNumber)
            Next
            d.Table(bt, {1.3F, 1.7F, 1.0F})
        End If

        d.Gap(4)
        ' Every receipt carries its own scannable code. The invoice number is
        ' already unique per sale, so the symbol is unique per receipt without
        ' inventing a second identifier that could drift out of step with it.
        ' Scanning it at the Receipts screen pulls this sale straight back up —
        ' for a return, a warranty claim, or a customer query at the counter.
        Try
            d.Barcode(Barcodes.Code128(number, heightPx:=52, moduleWidth:=2, showText:=False),
                      "Scan to look this receipt up  ·  " & number)
        Catch
            ' A symbol is a convenience; never lose the receipt over one.
        End Try

        d.Stamp()
        d.BrandFooter("Thanks for your patronage.")
        Return d
    End Function

    Private Shared Function OneLine(ParamArray parts As String()) As String
        Return String.Join(", ", parts.Where(Function(p) Not String.IsNullOrWhiteSpace(p)))
    End Function

    ''' The designed receipt itself (logo, bank details…) in the scrollable viewer,
    ''' with Save as PDF / Print / zoom on its toolbar.
    Private Function BuildReceiptTab() As TabPage
        Dim tp As New TabPage("Receipt")
        Dim viewer = BuildReceiptDoc().CreateViewer()
        viewer.Dock = DockStyle.Fill
        tp.Controls.Add(viewer)
        Return tp
    End Function

    ' ===== Layman breakdown =====

    Private Function BuildBreakdownTab() As TabPage
        Dim tp As New TabPage("What this means")
        Dim rtb As New RichTextBox() With {.Dock = DockStyle.Fill, .ReadOnly = True, .BorderStyle = BorderStyle.None,
            .Font = New Font("Segoe UI", Math.Max(9, Theme.BaseFontSize - 1))}
        rtb.Text = BuildLaymanText()
        tp.Controls.Add(rtb)
        Return tp
    End Function

    Private Function BuildLaymanText() As String
        Dim total = Convert.ToDecimal(_header("TotalAmount"))
        Dim vat = Convert.ToDecimal(_header("VATAmount"))
        Dim cost = Convert.ToDecimal(_header("TotalCost"))
        Dim revenue = total - vat
        Dim profit = revenue - cost
        Dim margin = If(revenue <> 0, profit / revenue * 100D, 0D)
        Dim units = _items.AsEnumerable().Sum(Function(r) Convert.ToInt32(r("Qty")))

        Dim sb As New System.Text.StringBuilder()
        sb.AppendLine($"1. WHAT WAS SOLD")
        sb.AppendLine($"   {units} item(s) across {_items.Rows.Count} product line(s), at {_header("PriceTier")} prices.")
        sb.AppendLine()
        sb.AppendLine($"2. GOODS VALUE (before discount & tax): {AppInfo.Money(_header("Subtotal"))}")
        sb.AppendLine()
        sb.AppendLine($"3. DISCOUNT GIVEN: {AppInfo.Money(_header("DiscountAmount"))} ({Convert.ToDecimal(_header("DiscountPct")):0.#}%)")
        sb.AppendLine()
        sb.AppendLine(If(vat > 0,
            $"4. VAT (government tax): {AppInfo.Money(vat)} — collected for the government, not your earnings.",
            "4. VAT: none charged on this sale."))
        sb.AppendLine()
        sb.AppendLine($"5. CUSTOMER PAYS IN TOTAL: {AppInfo.Money(total)}  (paid now {AppInfo.Money(_header("AmountPaid"))}, still owes {AppInfo.Money(Bal())} on this invoice)")
        Dim owedElsewhere = OtherBalance()
        If owedElsewhere > 0 Then
            sb.AppendLine()
            sb.AppendLine($"5b. THIS CUSTOMER ALSO OWES {AppInfo.Money(owedElsewhere)} FROM EARLIER PURCHASES.")
            sb.AppendLine($"    Total owed across everything: {AppInfo.Money(Bal() + owedElsewhere)}.")
        End If
        sb.AppendLine()
        sb.AppendLine($"6. WHAT THE GOODS COST YOU: {AppInfo.Money(cost)}")
        sb.AppendLine()
        sb.AppendLine($"7. YOUR GROSS PROFIT: {AppInfo.Money(revenue)} (sales excl. VAT) - {AppInfo.Money(cost)} = {AppInfo.Money(profit)}")
        sb.AppendLine()
        sb.AppendLine($"8. IN PLAIN TERMS: for every {AppInfo.CurrencySymbol}100 of goods sold you kept about {AppInfo.CurrencySymbol}{margin:0} as profit ({margin:0.#}% margin).")
        sb.AppendLine()
        sb.AppendLine($"9. REBATE: this customer's rebate balance with you is {AppInfo.Money(_rebate)}, redeemable as goods.")
        If profit < 0 Then
            sb.AppendLine()
            sb.AppendLine("!! This sale lost money — the goods cost more than the customer paid (excl. VAT). Check the discount and tier.")
        End If
        Return sb.ToString()
    End Function

End Class
