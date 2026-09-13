Imports System.Data
Imports System.IO
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Printed documents: receipts and waybills must always come to one page with
''' nothing cut, long reports must run over several pages, and "Save as PDF"
''' must produce a real file.
<TestClass>
Public Class DocumentTests

    Private Shared Function Lines(count As Integer) As DataTable
        Dim t As New DataTable()
        For Each c In {"#", "Description", "Qty", "Unit price", "Amount"}
            t.Columns.Add(c)
        Next
        For i = 1 To count
            t.Rows.Add(i, $"Premium Adult Dog Food with a deliberately long description, item {i} (20kg bag)", "12", "11,500.00", "138,000.00")
        Next
        Return t
    End Function

    Private Shared Function Receipt(lineCount As Integer) As DocPrinter
        Dim d As New DocPrinter() With {.DocTitle = "Receipt TEST", .FitToOnePage = True}
        d.Letterhead("SALES RECEIPT", {("Invoice no.", "INV-TEST-1"), ("Date", "12 Sep 2026"), ("Status", "PAID")})
        d.Panels(
            ("Bill to", {("Customer", "A Very Long Customer Business Name Limited", True), ("Address", "12 Long Street, Ikeja, Lagos State", False)}),
            ("Sale details", {("Price tier", "Wholesaler", False), ("Payment", "Bank Transfer", False)}))
        d.Table(Lines(lineCount), {0.45F, 4.4F, 0.9F, 1.6F, 1.7F}, rightAlignFrom:=2)
        d.Totals({("Subtotal", "₦1,656,000.00", 0), ("TOTAL", "₦1,656,000.00", 1), ("Balance due", "₦0.00", 0)})
        d.BrandFooter("Thanks for your patronage.")
        Return d
    End Function

    <TestMethod>
    Public Sub A_short_receipt_is_one_page()
        Assert.AreEqual(1, Receipt(3).PageCount())
    End Sub

    <TestMethod>
    Public Sub A_long_receipt_still_fits_one_page()
        Assert.AreEqual(1, Receipt(60).PageCount(), "fit-to-one-page must scale the whole receipt down")
    End Sub

    <TestMethod>
    Public Sub A_very_long_receipt_still_fits_one_page()
        Assert.AreEqual(1, Receipt(120).PageCount())
    End Sub

    <TestMethod>
    Public Sub Reports_run_onto_further_pages_instead_of_being_cut_off()
        Dim d As New DocPrinter() With {.DocTitle = "Report TEST", .Landscape = True}
        d.Letterhead("BUSINESS REPORT", {("Period", "Full year 2026")})
        d.Table(Lines(120))
        Assert.IsTrue(d.PageCount() > 1, "a 120-row report cannot fit one page — it must paginate")
    End Sub

    <TestMethod>
    Public Sub Save_as_pdf_writes_a_pdf_file()
        If Not DocPrinter.PdfPrinterAvailable() Then
            Assert.Inconclusive("Microsoft Print to PDF is not installed on this machine")
        End If
        Dim pdfPath = Path.Combine(Path.GetTempPath(), "chewystock_test_" & Guid.NewGuid().ToString("N").Substring(0, 6) & ".pdf")
        Try
            Assert.AreEqual("", Receipt(5).SaveAsPdfTo(pdfPath))
            ' Printing to file returns before the spooler finishes writing.
            For i = 1 To 40
                If File.Exists(pdfPath) AndAlso New FileInfo(pdfPath).Length > 1000 Then Exit For
                Threading.Thread.Sleep(250)
            Next
            Assert.IsTrue(File.Exists(pdfPath), "no PDF was written")
            Using fs = File.OpenRead(pdfPath)
                Dim head(3) As Byte
                fs.Read(head, 0, 4)
                Assert.AreEqual("%PDF", Text.Encoding.ASCII.GetString(head), "the file must really be a PDF")
            End Using
        Finally
            Try
                If File.Exists(pdfPath) Then File.Delete(pdfPath)
            Catch
            End Try
        End Try
    End Sub

    <TestMethod>
    Public Sub Waybill_style_document_with_signatures_is_one_page()
        Dim goods As New DataTable()
        For Each c In {"#", "SKU", "Description", "Unit", "Qty", "Received"}
            goods.Columns.Add(c)
        Next
        For i = 1 To 25
            goods.Rows.Add(i, "SKU-100" & i, "Chewy Pet feed variety " & i, "Bag", "40", "")
        Next
        Dim d As New DocPrinter() With {.DocTitle = "Waybill TEST", .FitToOnePage = True}
        d.Letterhead("WAYBILL / DELIVERY NOTE", {("Waybill no.", "WB-TEST-1"), ("Date issued", "12 Sep 2026")})
        d.Panels(
            ("Deliver to", {("Customer", "Companion Pets Abuja", True)}),
            ("Dispatch", {("From", "Lawal warehouse", True)}),
            ("Carrier", {("Driver", "Musa Bello", True)}))
        d.Table(goods, Nothing, rightAlignFrom:=4, footer:={"", "", "Total — 25 line(s)", "", "1,000", ""})
        d.Signatures("Dispatched by", "Driver", "Received by (customer)")
        d.BrandFooter("Thanks for your patronage.")
        Assert.AreEqual(1, d.PageCount())
    End Sub

End Class
