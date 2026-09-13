Imports System.Data
Imports System.Windows.Forms
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Money formatting, grid presentation and the theme defaults.
<TestClass>
Public Class PresentationTests

    <TestMethod>
    Public Sub Money_is_formatted_for_reading_and_for_documents()
        Assert.AreEqual("₦1,234,500", AppInfo.Money(1234500D))
        Assert.AreEqual("₦1,234,500.00", AppInfo.Money2(1234500D))
        Assert.AreEqual("₦0", AppInfo.Money(Nothing))
        Assert.AreEqual("₦0.00", AppInfo.Money2(DBNull.Value))
    End Sub

    <TestMethod>
    Public Sub Unfilled_settings_print_as_nothing()
        Assert.AreEqual("12 Feed Mill Road, Lagos", AppInfo.CompanyAddress)
        Assert.AreEqual("", AppInfo.CompanyPhone, "a [placeholder] value must never reach a receipt")
    End Sub

    <TestMethod>
    Public Sub Both_bank_accounts_are_offered_for_receipts()
        Dim banks = AppInfo.BankAccounts()
        Assert.AreEqual(2, banks.Count)
        Assert.AreEqual("FirstMonie Bank", banks(0).Bank)
        Assert.AreEqual("8676752988", banks(0).AccountNumber)
        Assert.AreEqual("First Bank PLC", banks(1).Bank)
        Assert.AreEqual("2047950632", banks(1).AccountNumber)
        Assert.IsTrue(banks.All(Function(b) b.AccountName = "ChewyPets Farm & Feeds"))
    End Sub

    <TestMethod>
    Public Sub Column_headings_are_written_for_people()
        Assert.AreEqual("Total amount", UiHelpers.FriendlyHeader("TotalAmount"))
        Assert.AreEqual("Invoice number", UiHelpers.FriendlyHeader("InvoiceNumber"))
        Assert.AreEqual("VAT rate", UiHelpers.FriendlyHeader("VATRate"))
        Assert.AreEqual("Invoice ID", UiHelpers.FriendlyHeader("InvoiceID"))
        Assert.AreEqual("SKU", UiHelpers.FriendlyHeader("SKU"))
        Assert.AreEqual("Produced on", UiHelpers.FriendlyHeader("ProducedOn"))
    End Sub

    <TestMethod>
    Public Sub Grids_right_align_money_and_show_readable_dates()
        Dim t As New DataTable()
        t.Columns.Add("InvoiceNumber", GetType(String))
        t.Columns.Add("TotalAmount", GetType(Decimal))
        t.Columns.Add("InvoiceDate", GetType(Date))
        t.Rows.Add("INV-1", 37121.82D, New Date(2026, 9, 9))

        Using host As New Form()
            Dim g = UiHelpers.NewGrid()
            host.Controls.Add(g)
            g.DataSource = t
            host.Show()
            Application.DoEvents()

            Assert.AreEqual("N2", g.Columns("TotalAmount").DefaultCellStyle.Format)
            Assert.AreEqual(DataGridViewContentAlignment.MiddleRight, g.Columns("TotalAmount").DefaultCellStyle.Alignment)
            Assert.AreEqual("dd MMM yyyy", g.Columns("InvoiceDate").DefaultCellStyle.Format)
            Assert.AreEqual("Invoice number", g.Columns("InvoiceNumber").HeaderText)
            Assert.AreEqual("37,121.82", g.Rows(0).Cells("TotalAmount").FormattedValue.ToString())
            Assert.AreEqual("09 Sep 2026", g.Rows(0).Cells("InvoiceDate").FormattedValue.ToString())
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub A_narrow_table_stretches_its_columns_to_fill_the_grid()
        Dim t As New DataTable()
        t.Columns.Add("Category", GetType(String))
        t.Columns.Add("Name", GetType(String))
        t.Rows.Add("Dog Food", "Adult Dog Food 20kg")
        ' Bind after the grid is on screen — matches real screens, which load
        ' their grid in a Load handler once the control is already parented and
        ' sized (Width/Height set before Show() aren't final until then).
        Using host As New Form() With {.Width = 900}
            Dim g = UiHelpers.NewGrid()
            host.Controls.Add(g)
            host.Show()
            Application.DoEvents()
            g.DataSource = t
            Application.DoEvents()
            Dim total = g.Columns.Cast(Of DataGridViewColumn)().Sum(Function(c) c.Width)
            Assert.IsTrue(total >= g.ClientSize.Width - 4, "a two-column table should fill the available width, not sit bunched on the left")
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub A_wide_table_keeps_readable_columns_and_stays_scrollable_rather_than_clipping_the_last_one()
        Dim t As New DataTable()
        For Each name In {"Category", "SKU", "ProductName", "Unit", "TotalQty", "Lawal", "Shore", "ProducedThisMonth", "ReorderLevel", "CostPrice", "PriceDistributor", "PriceWholesaler", "PriceRetail"}
            t.Columns.Add(name, GetType(String))
        Next
        t.Rows.Add("Dog Food", "SKU-1001", "Adult Dog Food 20kg", "Bag", "41", "41", "0", "0", "30", "8,500.00", "10,600.00", "10,100.00", "9,600.00")
        Using host As New Form() With {.Width = 900}
            Dim g = UiHelpers.NewGrid()
            host.Controls.Add(g)
            host.Show()
            Application.DoEvents()
            g.DataSource = t
            Application.DoEvents()

            Assert.AreEqual("Product name", g.Columns("ProductName").HeaderText)
            Assert.IsTrue(g.Columns("PriceWholesaler").Width > 40, "the last column must keep a real, readable width")
            Assert.IsTrue(g.DisplayedColumnCount(False) < g.Columns.Count,
                          "not every column can fit — the grid must scroll instead of squeezing them all in")
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub The_soft_theme_is_the_default_and_the_brand_theme_is_still_offered()
        Assert.AreEqual("Soft Mist", Theme.Palettes(0).Name)
        Assert.IsTrue(Theme.Palettes.Any(Function(p) p.Name = "Chewy Purple"))
        Assert.AreEqual("ChewyStock", Theme.AppName)
    End Sub

    <TestMethod>
    Public Sub Only_english_and_igbo_are_offered()
        Assert.AreEqual(2, Lang.Available.Length)
        Assert.AreEqual("en", Lang.Available(0).Code)
        Assert.AreEqual("English", Lang.Available(0).Name)
        Assert.AreEqual("ig", Lang.Available(1).Code)
        Assert.AreEqual("Igbo", Lang.Available(1).Name)
        Assert.IsFalse(Lang.IsRightToLeft)
    End Sub

    <TestMethod>
    Public Sub Switching_to_igbo_translates_the_screens_and_english_switches_back()
        Try
            Lang.SetLanguage("ig")
            Assert.AreEqual("ig", Lang.CurrentCode)
            Assert.AreEqual("Ndị ahịa", Lang.T("Customers"))
            Assert.AreEqual("Ire ahịa", Lang.T("Sales"))
            Assert.AreEqual("Dekọọ ihe e mepụtara", Lang.T("Record production"))
            Assert.AreEqual("Something untranslated", Lang.T("Something untranslated"), "unknown text stays as it is")

            Lang.SetLanguage("en")
            Assert.AreEqual("Customers", Lang.T("Customers"))
        Finally
            Lang.SetLanguage("en")
        End Try
    End Sub

    <TestMethod>
    Public Sub A_screens_captions_round_trip_between_igbo_and_english()
        ' Reproduces the reported bug: switching a live screen to Igbo and back
        ' to English must restore the original English captions, not get stuck.
        Using host As New Form()
            Dim btnSales As New Button() With {.Text = "Sales"}
            Dim btnCustomers As New Button() With {.Text = "Customers"}
            host.Controls.Add(btnSales)
            host.Controls.Add(btnCustomers)
            Try
                Lang.SetLanguage("ig")
                Lang.ApplyByText(host)
                Assert.AreEqual("Ire ahịa", btnSales.Text)
                Assert.AreEqual("Ndị ahịa", btnCustomers.Text)

                Lang.SetLanguage("en")
                Lang.ApplyByText(host)
                Assert.AreEqual("Sales", btnSales.Text, "must return to English, not stay in Igbo")
                Assert.AreEqual("Customers", btnCustomers.Text)

                ' And back to Igbo again, from the restored English captions.
                Lang.SetLanguage("ig")
                Lang.ApplyByText(host)
                Assert.AreEqual("Ire ahịa", btnSales.Text)
                Assert.AreEqual("Ndị ahịa", btnCustomers.Text)
            Finally
                Lang.SetLanguage("en")
            End Try
        End Using
    End Sub

    <TestMethod>
    Public Sub An_unknown_language_code_falls_back_to_english()
        Try
            Lang.SetLanguage("fr")   ' French was removed
            Assert.AreEqual("en", Lang.CurrentCode)
        Finally
            Lang.SetLanguage("en")
        End Try
    End Sub

    <TestMethod>
    Public Sub Branding_files_ship_with_the_app()
        For Each fileName In {"logo.png", "signature.png", "landing.jpg", "landingvideo.mp4", "app.ico"}
            Assert.IsTrue(IO.File.Exists(AppPaths.Asset(fileName)), "missing asset: " & fileName)
        Next
        Assert.IsNotNull(Theme.Logo, "the logo must load")
        Assert.IsNotNull(Theme.Signature, "the receipt signature must load")
    End Sub

End Class
