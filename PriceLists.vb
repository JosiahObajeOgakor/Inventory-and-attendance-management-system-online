Imports System.Data

''' The catalogue / price list a business sends its customers: every active
''' product at one price tier, grouped by category, with the bank details and
''' the company stamp — the same letterhead and signature as its receipts.
'''
''' "Sending" is deliberately manual. The PDF is saved, then the email app opens
''' with the message written and the folder open beside it, and a person drags
''' the file in and presses Send. Nothing leaves the office without someone
''' looking at it, and no email password has to be stored in the app.
Public Module PriceLists

    Public ReadOnly Property Tiers As String() = {"Distributor", "Wholesaler", "Retailer"}

    ''' A customer's ranking decides the prices they're shown; walk-ins pay retail.
    Public Function TierFor(customerType As String) As String
        Return If(Tiers.Contains(customerType), customerType, "Retailer")
    End Function

    ''' Column names are chosen here, never taken from the caller, so the tier
    ''' can go into the SQL text safely.
    Private Function PriceColumn(tier As String) As String
        Select Case tier
            Case "Distributor" : Return "p.PriceDistributor"
            Case "Wholesaler" : Return "p.PriceWholesaler"
            Case Else : Return "p.PriceRetail"
        End Select
    End Function

    ''' The products on the list. Anything not priced at this tier is left off
    ''' rather than printed at ₦0.00; out-of-stock lines only when asked for.
    Public Function Lines(tier As String, includeOutOfStock As Boolean) As DataTable
        Return DataAccess.GetTable(
            $"SELECT c.Name AS Category, p.Name AS Product, p.SKU, p.Unit, {PriceColumn(tier)} AS Price, " &
            "ISNULL(st.Qty, 0) AS InStock " &
            "FROM Products p JOIN Categories c ON c.CategoryID = p.CategoryID " &
            "LEFT JOIN (SELECT ProductID, SUM(QuantityOnHand) AS Qty FROM StockBatches GROUP BY ProductID) st ON st.ProductID = p.ProductID " &
            $"WHERE p.IsActive = 1 AND {PriceColumn(tier)} > 0 AND (@all = 1 OR ISNULL(st.Qty, 0) > 0) " &
            "ORDER BY c.Name, p.Name",
            New Dictionary(Of String, Object) From {{"@all", includeOutOfStock}})
    End Function

    Public Function Build(tier As String, Optional customerName As String = Nothing,
                          Optional includeOutOfStock As Boolean = False) As DocPrinter
        Dim today = Date.Today
        Dim d As New DocPrinter() With {
            .DocTitle = $"{Company.Current.DisplayName} price list {today:dd MMM yyyy}" &
                        If(String.IsNullOrWhiteSpace(customerName), "", " - " & customerName),
            .Watermark = True,
            .FooterText = $"{AppInfo.CompanyName}   ·   Price list ({tier})   ·   {today:dd MMM yyyy}"}

        Dim updated = If(PriceBook.HasTables(), PriceBook.LastUpdated(), CType(Nothing, Date?))
        d.Letterhead("PRICE LIST", {
            ("Date", today.ToString("dd MMM yyyy")),
            ("Prices updated", If(updated.HasValue, updated.Value.ToString("dd MMM yyyy"), "")),
            ("Prices", tier),
            ("Prepared for", If(customerName, ""))})

        d.Text($"Prices are in {AppInfo.CurrencySymbol} per unit, correct on {today:dd MMMM yyyy} and subject to change " &
               "without notice. VAT is added on the invoice where it applies.", grey:=True)
        d.Gap(6)

        Dim rows = Lines(tier, includeOutOfStock)
        If rows.Rows.Count = 0 Then
            d.Text("No products are priced for this tier yet.", bold:=True)
        End If

        ' One table per category, numbered straight through the whole list so a
        ' customer can order by line number over the phone.
        Dim n = 0
        For Each group In rows.AsEnumerable().GroupBy(Function(r) Convert.ToString(r("Category")))
            d.SectionTitle(group.Key)
            Dim table As New DataTable()
            For Each col In {"#", "Product", "Code", "Unit", "Availability", $"Price ({AppInfo.CurrencySymbol})"}
                table.Columns.Add(col)
            Next
            For Each r In group
                n += 1
                ' Customers see whether they can have it, not how many we hold.
                table.Rows.Add(n, r("Product"), r("SKU"), r("Unit"),
                               If(Convert.ToInt32(r("InStock")) > 0, "In stock", "Out of stock"),
                               Convert.ToDecimal(r("Price")).ToString("N2"))
            Next
            d.Table(table, {0.45F, 3.6F, 1.2F, 0.9F, 1.2F, 1.4F}, rightAlignFrom:=5)
            d.Gap(6)
        Next

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

        Dim contact = String.Join("  ·  ", {AppInfo.CompanyPhone, AppInfo.CompanyEmail}.Where(Function(s) s <> ""))
        If contact <> "" Then
            d.Gap(4)
            d.Text("To order: " & contact, bold:=True)
        End If

        ' A dated code on every list, so a customer's copy can be matched to the
        ' prices that were current when it was sent.
        d.Gap(8)
        Try
            Dim reference = ReferenceFor(today)
            d.Barcode(Barcodes.Code128(reference, heightPx:=46, moduleWidth:=2, showText:=False),
                      "Price list reference  ·  " & reference)
        Catch
        End Try
        d.Stamp()
        d.BrandFooter("Thank you for doing business with us.")
        Return d
    End Function

    ''' "CandidPurrfect-PL-14092026" — the code printed on that day's price list.
    Public Function ReferenceFor(day As Date) As String
        Return $"{Company.Current.DocumentPrefix}-PL-{day:ddMMyyyy}"
    End Function

    Public Function EmailSubject() As String
        Return $"{AppInfo.CompanyName} — price list, {Date.Today:dd MMM yyyy}"
    End Function

    Public Function EmailBody(tier As String, Optional customerName As String = Nothing) As String
        Dim sb As New System.Text.StringBuilder()
        sb.AppendLine($"Dear {If(String.IsNullOrWhiteSpace(customerName), "Customer", customerName.Trim())},")
        sb.AppendLine()
        sb.AppendLine($"Please find attached our current price list ({tier} prices, {Date.Today:dd MMMM yyyy}).")
        sb.AppendLine()
        sb.AppendLine("To place an order, simply reply to this email with the products and quantities you need.")
        Dim banks = AppInfo.BankAccounts()
        If banks.Count > 0 Then
            sb.AppendLine()
            sb.AppendLine("Payment can be made by transfer to:")
            For Each b In banks
                sb.AppendLine($"  {b.Bank} — {b.AccountNumber} ({b.AccountName})")
            Next
        End If
        sb.AppendLine()
        sb.AppendLine("Kind regards,")
        sb.Append(AppInfo.CompanyName)
        Return sb.ToString()
    End Function

    ''' A mailto: link that opens the email app with everything but the attachment
    ''' filled in (mailto has no way to attach a file — that's the manual step).
    Public Function MailtoLink(toAddress As String, subject As String, body As String) As String
        Return "mailto:" & If(toAddress, "").Trim() &
               "?subject=" & Uri.EscapeDataString(subject) &
               "&body=" & Uri.EscapeDataString(body.Replace(vbCrLf, vbLf).Replace(vbLf, vbCrLf))
    End Function

    Public Function IsPlausibleEmail(address As String) As Boolean
        If String.IsNullOrWhiteSpace(address) Then Return False
        Dim a = address.Trim()
        Dim at = a.IndexOf("@"c)
        Return at > 0 AndAlso a.LastIndexOf("."c) > at + 1 AndAlso Not a.Contains(" ") AndAlso a.Count(Function(ch) ch = "@"c) = 1
    End Function

End Module
