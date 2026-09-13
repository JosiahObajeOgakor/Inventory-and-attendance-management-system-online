Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.Text.RegularExpressions

''' Barcode and QR symbols for shelf labels, bin cards, invoices and serial tags.
'''
''' Code 128-B is drawn here rather than pulled from a library: it is a widths
''' table and a modulo-103 checksum, and every hand scanner on the market reads
''' it. QR needs Reed-Solomon and mask scoring, so that one comes from QRCoder.
Public Module Barcodes

    ''' Bar/space module widths for Code 128 values 0-106. Each entry is six
    ''' digits — bar, space, bar, space, bar, space — except the stop pattern,
    ''' which carries a seventh module as its terminating bar.
    Private ReadOnly Code128Patterns As String() = {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112"}

    Private Const StartB As Integer = 104
    Private Const Stop_ As Integer = 106

    ''' True when every character can travel in Code 128 set B (printable ASCII).
    Public Function CanEncode128(value As String) As Boolean
        If String.IsNullOrEmpty(value) Then Return False
        Return value.All(Function(c) Asc(c) >= 32 AndAlso Asc(c) <= 126)
    End Function

    ''' Draws `value` as a Code 128-B symbol. `moduleWidth` is the width in pixels
    ''' of the narrowest bar — 2 prints reliably on a thermal label, 1 is the
    ''' floor for a laser printer at 300dpi.
    Public Function Code128(value As String, Optional heightPx As Integer = 60,
                            Optional moduleWidth As Integer = 2,
                            Optional showText As Boolean = True) As Bitmap
        If Not CanEncode128(value) Then Throw New ArgumentException("Code 128-B only carries printable ASCII: " & value)

        Dim codes As New List(Of Integer) From {StartB}
        For Each c In value
            codes.Add(Asc(c) - 32)
        Next
        Dim sum = StartB
        For i = 1 To codes.Count - 1
            sum += codes(i) * i
        Next
        codes.Add(sum Mod 103)
        codes.Add(Stop_)

        Dim modules = codes.Sum(Function(code) Code128Patterns(code).Sum(Function(d) Integer.Parse(d.ToString())))
        Const Quiet As Integer = 10 ' modules of clear space each side, per the spec
        Dim textHeight = If(showText, 16, 0)
        Dim width = (modules + Quiet * 2) * moduleWidth

        Dim bmp As New Bitmap(width, heightPx + textHeight, PixelFormat.Format32bppArgb)
        Using g = Graphics.FromImage(bmp)
            g.Clear(Color.White)
            Dim x = Quiet * moduleWidth
            For Each code In codes
                Dim bar = True
                For Each d In Code128Patterns(code)
                    Dim w = Integer.Parse(d.ToString()) * moduleWidth
                    If bar Then g.FillRectangle(Brushes.Black, x, 0, w, heightPx)
                    x += w
                    bar = Not bar
                Next
            Next
            If showText Then
                Using f As New Font("Consolas", 9F), sf As New StringFormat() With {.Alignment = StringAlignment.Center}
                    g.DrawString(value, f, Brushes.Black, New RectangleF(0, heightPx + 1, width, textHeight), sf)
                End Using
            End If
        End Using
        Return bmp
    End Function

    ''' Draws `value` as a QR symbol at error-correction level Q, which survives a
    ''' scuffed or partly torn label better than the usual level M.
    Public Function Qr(value As String, Optional pixelsPerModule As Integer = 6) As Bitmap
        If String.IsNullOrEmpty(value) Then Throw New ArgumentException("Nothing to encode.")
        Using gen As New QRCoder.QRCodeGenerator()
            Using data = gen.CreateQrCode(value, QRCoder.QRCodeGenerator.ECCLevel.Q)
                Using code As New QRCoder.QRCode(data)
                    Return code.GetGraphic(pixelsPerModule, Color.Black, Color.White, True)
                End Using
            End Using
        End Using
    End Function

    ''' EAN-13 check digit: odd positions weigh 1, even positions weigh 3, and the
    ''' digit is whatever lifts the total to the next multiple of ten.
    Public Function Ean13CheckDigit(twelveDigits As String) As Integer
        If twelveDigits Is Nothing OrElse twelveDigits.Length <> 12 OrElse Not twelveDigits.All(AddressOf Char.IsDigit) Then
            Throw New ArgumentException("EAN-13 needs exactly 12 digits before the check digit.")
        End If
        Dim sum = 0
        For i = 0 To 11
            sum += (Asc(twelveDigits(i)) - Asc("0"c)) * If(i Mod 2 = 0, 1, 3)
        Next
        Return (10 - (sum Mod 10)) Mod 10
    End Function

    Public Function IsValidEan13(value As String) As Boolean
        If value Is Nothing OrElse value.Length <> 13 OrElse Not value.All(AddressOf Char.IsDigit) Then Return False
        Return Ean13CheckDigit(value.Substring(0, 12)) = Asc(value(12)) - Asc("0"c)
    End Function

    ''' Mints an in-store barcode for a product the supplier shipped unlabelled.
    ''' Prefix 200 is the GS1 range reserved for internal use, so these can never
    ''' collide with a real manufacturer's code.
    Public Function MintInternalBarcode(productId As Integer) As String
        Dim body = "200" & productId.ToString("D9")
        Return body & Ean13CheckDigit(body).ToString()
    End Function

    ''' The payload printed as QR on a serialised unit's tag. Kept deliberately
    ''' short and human-readable so it also works typed in by hand.
    Public Function SerialTagPayload(sku As String, serialNumber As String) As String
        Return $"CS|S|{sku}|{serialNumber}"
    End Function

    ''' The payload printed as QR on an invoice, for a customer to scan and check.
    Public Function InvoiceTagPayload(invoiceNumber As String, total As Decimal) As String
        Return $"CS|I|{invoiceNumber}|{total:0.00}"
    End Function

    Private ReadOnly TagPattern As New Regex("^CS\|(?<kind>[SI])\|(?<a>[^|]*)\|(?<b>[^|]*)$", RegexOptions.Compiled)

    ''' What a scanner just sent us. A hand scanner is a keyboard: it types the
    ''' payload then presses Enter, so the till only ever sees a finished string.
    Public Class Scan
        Public Property Kind As String   ' "Serial", "Invoice" or "Barcode"
        Public Property Code As String   ' serial number, invoice number, or raw barcode
        Public Property Sku As String    ' set for serial tags
    End Class

    ''' Interprets scanner input: our own QR tags first, then anything else as a
    ''' plain product barcode.
    Public Function ReadScan(input As String) As Scan
        Dim raw = If(input, "").Trim()
        If raw.Length = 0 Then Return Nothing
        Dim m = TagPattern.Match(raw)
        If m.Success Then
            If m.Groups("kind").Value = "S" Then
                Return New Scan() With {.Kind = "Serial", .Sku = m.Groups("a").Value, .Code = m.Groups("b").Value}
            End If
            Return New Scan() With {.Kind = "Invoice", .Code = m.Groups("a").Value}
        End If
        Return New Scan() With {.Kind = "Barcode", .Code = raw}
    End Function

    ''' A print-ready shelf label: name, price, and the scannable symbol.
    Public Function ShelfLabel(productName As String, sku As String, barcode As String, price As Decimal) As Bitmap
        Dim bars = Code128(barcode)
        Dim bmp As New Bitmap(Math.Max(320, bars.Width + 24), 150, PixelFormat.Format32bppArgb)
        Using g = Graphics.FromImage(bmp)
            g.Clear(Color.White)
            g.TextRenderingHint = Text.TextRenderingHint.ClearTypeGridFit
            Using title As New Font("Segoe UI", 10F, FontStyle.Bold),
                  small As New Font("Segoe UI", 8F),
                  money As New Font("Segoe UI", 13F, FontStyle.Bold)
                g.DrawString(Truncate(productName, 34), title, Brushes.Black, 12, 8)
                g.DrawString(sku, small, Brushes.DimGray, 12, 28)
                g.DrawString(price.ToString("₦#,##0.00"), money, Brushes.Black, 12, 44)
            End Using
            g.DrawImage(bars, 12, 72)
            g.DrawRectangle(Pens.LightGray, 0, 0, bmp.Width - 1, bmp.Height - 1)
        End Using
        bars.Dispose()
        Return bmp
    End Function

    Private Function Truncate(s As String, max As Integer) As String
        If String.IsNullOrEmpty(s) OrElse s.Length <= max Then Return If(s, "")
        Return s.Substring(0, max - 1) & "…"
    End Function

End Module
