Imports System.Windows.Forms
Imports System.Drawing
Imports System.Drawing.Printing
Imports System.Data
Imports System.IO

''' Document builder for receipts, waybills and reports. Append blocks
''' (letterhead, detail panels, tables, totals, signatures, text), then call
''' ShowPreview() / CreateViewer() for the on-screen viewer (scroll, zoom,
''' Save as PDF, Print) or SaveAsPdf().
'''
''' Every block lays itself out through one Render(draw:=False/True) routine, so
''' measuring and painting can never disagree. FitToOnePage shrinks the whole
''' document uniformly until it fits a single A4 page — text is wrapped, never
''' cut short.
Public NotInheritable Class DocPrinter

    ' ===== house style =====
    Private Shared ReadOnly Accent As Color = Color.FromArgb(106, 40, 160)
    Private Shared ReadOnly AccentSoft As Color = Color.FromArgb(242, 235, 250)
    Private Shared ReadOnly Ink As Color = Color.FromArgb(28, 28, 34)
    Private Shared ReadOnly Muted As Color = Color.FromArgb(100, 100, 112)
    Private Shared ReadOnly Hairline As Color = Color.FromArgb(214, 214, 222)
    Private Shared ReadOnly Stripe As Color = Color.FromArgb(247, 247, 250)
    Private Const Face As String = "Segoe UI"

    Private Shared ReadOnly WrapNear As New StringFormat()
    Private Shared ReadOnly WrapFar As New StringFormat() With {.Alignment = StringAlignment.Far}
    Private Shared ReadOnly WrapCenter As New StringFormat() With {.Alignment = StringAlignment.Center}

    Private Shared Function Fmt(a As StringAlignment) As StringFormat
        Select Case a
            Case StringAlignment.Far : Return WrapFar
            Case StringAlignment.Center : Return WrapCenter
            Case Else : Return WrapNear
        End Select
    End Function

    ''' Height of `s` wrapped to width `w` (at least one line).
    Private Shared Function TextH(g As Graphics, s As String, f As Font, w As Single) As Single
        If String.IsNullOrEmpty(s) Then Return f.GetHeight(g)
        Return g.MeasureString(s, f, New SizeF(Math.Max(4, w), 100000), WrapNear).Height
    End Function

    ''' Draws `s` wrapped inside width `w`; returns the height used.
    Private Shared Function Put(g As Graphics, draw As Boolean, s As String, f As Font, c As Color,
                                x As Single, y As Single, w As Single, Optional align As StringAlignment = StringAlignment.Near) As Single
        Dim h = TextH(g, s, f, w)
        If draw AndAlso Not String.IsNullOrEmpty(s) Then
            Using b As New SolidBrush(c)
                g.DrawString(s, f, b, New RectangleF(x, y, Math.Max(4, w), h + 2), Fmt(align))
            End Using
        End If
        Return h
    End Function

    Private Shared Sub HLine(g As Graphics, c As Color, x1 As Single, x2 As Single, y As Single, Optional t As Single = 1)
        Using p As New Pen(c, t)
            g.DrawLine(p, x1, y, x2, y)
        End Using
    End Sub

    ' ===== blocks =====

    ''' A barcode or QR symbol, centred, scaled down if it would overrun the
    ''' column but never scaled up — enlarging a symbol past its natural size
    ''' blurs the bars and scanners start missing it.
    Private Class BarcodeBlock
        Inherits Block

        Public Symbol As Image
        Public Caption As String
        Public CaptionFont As Font
        Public CaptionColour As Color

        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            If Symbol Is Nothing Then Return 0

            Dim scale = Math.Min(1.0F, w / CSng(Symbol.Width))
            Dim sw = Symbol.Width * scale
            Dim sh = Symbol.Height * scale
            Dim sx = x + (w - sw) / 2

            Dim captionH As Single = 0
            If Not String.IsNullOrEmpty(Caption) Then captionH = CaptionFont.GetHeight(g) + 2

            If draw Then
                ' Nearest-neighbour: smoothing a barcode softens the bar edges,
                ' which is exactly what a scanner needs kept sharp.
                Dim previous = g.InterpolationMode
                g.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor
                g.DrawImage(Symbol, sx, y, sw, sh)
                g.InterpolationMode = previous

                If captionH > 0 Then
                    Using b As New SolidBrush(CaptionColour),
                          sf As New StringFormat() With {.Alignment = StringAlignment.Center}
                        g.DrawString(Caption, CaptionFont, b, New RectangleF(x, y + sh + 1, w, captionH), sf)
                    End Using
                End If
            End If
            Return sh + captionH + 6
        End Function
    End Class

    Private MustInherit Class Block
        ''' Lays out at (x, y) within width w — and paints when draw=True.
        ''' Returns the height used, identical in both modes.
        Public MustOverride Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
        Public Overridable Sub Reset()
        End Sub
        ''' Height that must fit before this block may start on the current page.
        Public Overridable Function StartHeight(g As Graphics, w As Single) As Single
            Return Render(g, 0, 0, w, False)
        End Function
        ''' Paged drawing; returns True once finished (only tables split).
        Public Overridable Function DrawPaged(g As Graphics, x As Single, ByRef y As Single, w As Single, bottom As Single) As Boolean
            y += Render(g, x, y, w, True)
            Return True
        End Function
    End Class

    Private Class TextBlock
        Inherits Block
        Public Text As String
        Public Font As Font
        Public Colour As Color = Ink
        Public Align As StringAlignment = StringAlignment.Near
        Public SpacingAfter As Single = 4
        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            Return Put(g, draw, Text, Font, Colour, x, y, w, Align) + SpacingAfter
        End Function
    End Class

    Private Class ImageBlock
        Inherits Block
        Public Img As Image
        Public MaxHeight As Single = 70
        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            If Img Is Nothing Then Return 0
            Dim s = Math.Min(MaxHeight / Img.Height, 260.0F / Img.Width)
            If draw Then g.DrawImage(Img, x, y, Img.Width * s, Img.Height * s)
            Return Img.Height * s + 8
        End Function
    End Class

    Private Class RuleBlock
        Inherits Block
        Public Colour As Color = Hairline
        Public Thickness As Single = 1
        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            If draw Then HLine(g, Colour, x, x + w, y + 5, Thickness)
            Return 11
        End Function
    End Class

    Private Class GapBlock
        Inherits Block
        Public Height As Single = 12
        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            Return Height
        End Function
    End Class

    ''' Label / value rows; values wrap in their own column.
    Private Class KeyValueBlock
        Inherits Block
        Public Pairs As New List(Of (K As String, V As String, Bold As Boolean))
        Public LabelWidth As Single = 150
        Private ReadOnly _font As New Font(Face, 9.5F)
        Private ReadOnly _bold As New Font(Face, 9.5F, FontStyle.Bold)
        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            Dim top = y
            For Each p In Pairs
                Dim lh = Put(g, draw, p.K, _font, Muted, x, y, LabelWidth - 8)
                Dim vh = Put(g, draw, If(p.V, ""), If(p.Bold, _bold, _font), Ink, x + LabelWidth, y, w - LabelWidth)
                y += Math.Max(lh, vh) + 3
            Next
            Return y - top + 4
        End Function
    End Class

    ''' Letterhead: logo + company on the left, document title + reference
    ''' details on the right, accent rule underneath.
    Private Class LetterheadBlock
        Inherits Block
        Public Logo As Image
        Public Company As String
        Public CompanyLines As New List(Of String)
        Public Title As String
        Public Meta As New List(Of (K As String, V As String))
        Private ReadOnly _name As New Font(Face, 15, FontStyle.Bold)
        Private ReadOnly _small As New Font(Face, 8.5F)
        Private ReadOnly _title As New Font(Face, 14, FontStyle.Bold)
        Private ReadOnly _metaK As New Font(Face, 8.5F)
        Private ReadOnly _metaV As New Font(Face, 8.5F, FontStyle.Bold)

        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            Dim leftW = w * 0.56F, rightW = w * 0.4F, rightX = x + w - rightW

            ' Left: logo, then company name + contact lines beside it.
            Dim logoW As Single = 0, logoH As Single = 0
            If Logo IsNot Nothing Then
                Dim s = Math.Min(70.0F / Logo.Height, 90.0F / Logo.Width)
                logoW = Logo.Width * s : logoH = Logo.Height * s
                If draw Then g.DrawImage(Logo, x, y, logoW, logoH)
            End If
            Dim tx = x + If(logoW > 0, logoW + 12, 0), tw = leftW - (tx - x)
            Dim ly = y
            ly += Put(g, draw, Company, _name, Accent, tx, ly, tw) + 1
            For Each line In CompanyLines
                ly += Put(g, draw, line, _small, Muted, tx, ly, tw)
            Next
            Dim leftH = Math.Max(logoH, ly - y)

            ' Right: title, then label ........ value rows.
            Dim ry = y
            ry += Put(g, draw, Title, _title, Ink, rightX, ry, rightW, StringAlignment.Far) + 4
            Dim labelW = rightW * 0.42F
            For Each m In Meta
                Dim kh = Put(g, draw, m.K, _metaK, Muted, rightX, ry, labelW)
                Dim vh = Put(g, draw, m.V, _metaV, Ink, rightX + labelW, ry, rightW - labelW, StringAlignment.Far)
                ry += Math.Max(kh, vh) + 1
            Next
            Dim h = Math.Max(leftH, ry - y) + 8
            If draw Then HLine(g, Accent, x, x + w, y + h, 2)
            Return h + 12
        End Function
    End Class

    ''' Side-by-side detail panels ("Bill to" | "Sale details" | …).
    Private Class PanelsBlock
        Inherits Block
        Public Panels As New List(Of (Title As String, Rows As List(Of (K As String, V As String, Bold As Boolean))))
        Private ReadOnly _title As New Font(Face, 8.5F, FontStyle.Bold)
        Private ReadOnly _k As New Font(Face, 8.5F)
        Private ReadOnly _v As New Font(Face, 9, FontStyle.Regular)
        Private ReadOnly _vb As New Font(Face, 9, FontStyle.Bold)
        Private Const GapX As Single = 22

        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            Dim n = Math.Max(1, Panels.Count)
            Dim pw = (w - GapX * (n - 1)) / n
            Dim maxH As Single = 0
            For i = 0 To Panels.Count - 1
                Dim px = x + i * (pw + GapX), py = y
                py += Put(g, draw, Panels(i).Title.ToUpperInvariant(), _title, Accent, px, py, pw) + 2
                If draw Then HLine(g, Hairline, px, px + pw, py)
                py += 5
                Dim lw = Math.Min(92, pw * 0.38F)
                For Each r In Panels(i).Rows
                    If String.IsNullOrWhiteSpace(r.V) Then Continue For
                    Dim kh = Put(g, draw, r.K, _k, Muted, px, py + 0.5F, lw - 6)
                    Dim vh = Put(g, draw, r.V, If(r.Bold, _vb, _v), Ink, px + lw, py, pw - lw)
                    py += Math.Max(kh, vh) + 2
                Next
                maxH = Math.Max(maxH, py - y)
            Next
            Return maxH + 10
        End Function
    End Class

    ''' Table with a coloured header band, wrapped cells (nothing is cut short),
    ''' per-column alignment and an optional bold footer row. Splits across pages
    ''' in paged mode, repeating the header.
    Private Class TableBlock
        Inherits Block
        Public Table As DataTable
        Public Weights As Single()
        Public RightAlignFrom As Integer = -1
        Public Footer As String()
        Public FontSize As Single = 9
        Private _head As Font, _cell As Font, _foot As Font
        Private _next As Integer
        Private Const PadX As Single = 5, PadY As Single = 3.5F

        Private Sub EnsureFonts()
            If _cell IsNot Nothing Then Return
            _head = New Font(Face, FontSize, FontStyle.Bold)
            _cell = New Font(Face, FontSize)
            _foot = New Font(Face, FontSize, FontStyle.Bold)
        End Sub

        Private Function Aligns() As StringAlignment()
            Dim n = Table.Columns.Count
            Dim a(n - 1) As StringAlignment
            For c = 0 To n - 1
                Dim t = Table.Columns(c).DataType
                Dim numeric = t Is GetType(Decimal) OrElse t Is GetType(Double) OrElse t Is GetType(Single) OrElse
                              t Is GetType(Integer) OrElse t Is GetType(Long) OrElse t Is GetType(Short)
                a(c) = If((RightAlignFrom >= 0 AndAlso c >= RightAlignFrom) OrElse (RightAlignFrom < 0 AndAlso numeric),
                          StringAlignment.Far, StringAlignment.Near)
            Next
            Return a
        End Function

        Private Function ColWidths(g As Graphics, w As Single) As Single()
            Dim n = Table.Columns.Count
            Dim weightsToUse As Single()
            If Weights IsNot Nothing AndAlso Weights.Length = n Then
                weightsToUse = Weights
            Else
                ' Size each column to its header / widest (sampled) value.
                weightsToUse = New Single(n - 1) {}
                Dim sample = Math.Min(Table.Rows.Count, 60)
                For c = 0 To n - 1
                    Dim cw = g.MeasureString(Table.Columns(c).ColumnName, _head).Width
                    For r = 0 To sample - 1
                        cw = Math.Max(cw, g.MeasureString(FormatCell(Table.Rows(r)(c)), _cell).Width)
                    Next
                    weightsToUse(c) = Math.Max(28, Math.Min(cw, 320)) + PadX * 2
                Next
            End If
            Dim total = weightsToUse.Sum()
            Return weightsToUse.Select(Function(v) v / total * w).ToArray()
        End Function

        Public Shared Function FormatCell(v As Object) As String
            If v Is Nothing OrElse TypeOf v Is DBNull Then Return ""
            If TypeOf v Is DateTime Then
                Dim d = DirectCast(v, DateTime)
                Return If(d.TimeOfDay = TimeSpan.Zero, d.ToString("dd MMM yyyy"), d.ToString("dd MMM yyyy HH:mm"))
            End If
            If TypeOf v Is Decimal Then Return DirectCast(v, Decimal).ToString("N2")
            If TypeOf v Is Double Then Return DirectCast(v, Double).ToString("N2")
            Return Convert.ToString(v)
        End Function

        Private Function Cells(r As Integer) As String()
            Return Table.Columns.Cast(Of DataColumn)().Select(Function(c) FormatCell(Table.Rows(r)(c))).ToArray()
        End Function

        ''' Draws one row of cells; returns its height.
        Private Function Row(g As Graphics, draw As Boolean, x As Single, y As Single, cw As Single(), al As StringAlignment(),
                             vals As String(), f As Font, fg As Color, bg As Color?) As Single
            Dim h As Single = 0
            For i = 0 To vals.Length - 1
                h = Math.Max(h, TextH(g, vals(i), f, cw(i) - PadX * 2))
            Next
            h += PadY * 2
            If draw Then
                If bg.HasValue Then
                    Using b As New SolidBrush(bg.Value)
                        g.FillRectangle(b, x, y, cw.Sum(), h)
                    End Using
                End If
                Dim cx = x
                For i = 0 To vals.Length - 1
                    Put(g, True, vals(i), f, fg, cx + PadX, y + PadY, cw(i) - PadX * 2, al(i))
                    cx += cw(i)
                Next
            End If
            Return h
        End Function

        Private Function HeaderRow(g As Graphics, draw As Boolean, x As Single, y As Single, cw As Single(), al As StringAlignment()) As Single
            Return Row(g, draw, x, y, cw, al, Table.Columns.Cast(Of DataColumn)().Select(Function(c) c.ColumnName).ToArray(), _head, Color.White, Accent)
        End Function

        Public Overrides Sub Reset()
            _next = 0
        End Sub

        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            EnsureFonts()
            Dim cw = ColWidths(g, w), al = Aligns()
            Dim top = y
            y += HeaderRow(g, draw, x, y, cw, al)
            For r = 0 To Table.Rows.Count - 1
                Dim rh = Row(g, draw, x, y, cw, al, Cells(r), _cell, Ink, If(r Mod 2 = 1, Stripe, CType(Nothing, Color?)))
                If draw Then HLine(g, Hairline, x, x + w, y + rh)
                y += rh
            Next
            y += FooterRow(g, draw, x, y, w, cw, al)
            Return y - top + 8
        End Function

        Private Function FooterRow(g As Graphics, draw As Boolean, x As Single, y As Single, w As Single, cw As Single(), al As StringAlignment()) As Single
            If Footer Is Nothing Then Return 0
            If draw Then HLine(g, Accent, x, x + w, y, 1.5F)
            Return Row(g, draw, x, y + 1, cw, al, Footer, _foot, Ink, AccentSoft) + 1
        End Function

        Public Overrides Function StartHeight(g As Graphics, w As Single) As Single
            EnsureFonts()
            Dim cw = ColWidths(g, w), al = Aligns()
            Dim h = HeaderRow(g, False, 0, 0, cw, al)
            If _next < Table.Rows.Count Then h += Row(g, False, 0, 0, cw, al, Cells(_next), _cell, Ink, Nothing)
            Return h
        End Function

        Public Overrides Function DrawPaged(g As Graphics, x As Single, ByRef y As Single, w As Single, bottom As Single) As Boolean
            EnsureFonts()
            Dim cw = ColWidths(g, w), al = Aligns()
            y += HeaderRow(g, True, x, y, cw, al)
            While _next < Table.Rows.Count
                Dim vals = Cells(_next)
                Dim rh = Row(g, False, x, y, cw, al, vals, _cell, Ink, Nothing)
                If y + rh > bottom Then Return False
                Row(g, True, x, y, cw, al, vals, _cell, Ink, If(_next Mod 2 = 1, Stripe, CType(Nothing, Color?)))
                HLine(g, Hairline, x, x + w, y + rh)
                y += rh
                _next += 1
            End While
            y += FooterRow(g, True, x, y, w, cw, al) + 8
            Return True
        End Function
    End Class

    ''' Money summary: a box on the right whose amounts line up with the table's
    ''' right edge; optional notes (payment method, due date, …) on the left.
    Private Class TotalsBlock
        Inherits Block
        Public Rows As New List(Of (K As String, V As String, Style As Integer))   ' 0 normal, 1 grand total, 2 warning
        Public Notes As New List(Of (Text As String, Bold As Boolean))
        Private ReadOnly _k As New Font(Face, 9)
        Private ReadOnly _v As New Font(Face, 9)
        Private ReadOnly _big As New Font(Face, 10.5F, FontStyle.Bold)
        Private ReadOnly _bold As New Font(Face, 9, FontStyle.Bold)
        Private ReadOnly _note As New Font(Face, 8.5F)
        Private ReadOnly _noteB As New Font(Face, 8.5F, FontStyle.Bold)

        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            Dim boxW = w * 0.42F, boxX = x + w - boxW
            Dim top = y
            Dim ry = y
            For Each r In Rows
                Dim f = If(r.Style = 1, _big, If(r.Style = 2, _bold, _v))
                Dim h = Math.Max(TextH(g, r.K, _k, boxW * 0.55F), TextH(g, r.V, f, boxW * 0.45F)) + 7
                If draw Then
                    If r.Style = 1 Then
                        Using b As New SolidBrush(AccentSoft)
                            g.FillRectangle(b, boxX, ry, boxW, h)
                        End Using
                        HLine(g, Accent, boxX, boxX + boxW, ry, 1.5F)
                    End If
                    Put(g, True, r.K, If(r.Style = 0, _k, _bold), If(r.Style = 0, Muted, Ink), boxX + 6, ry + 3.5F, boxW * 0.55F - 6)
                    ' Right padding matches the table's cell padding, so amounts line up with its last column.
                    Put(g, True, r.V, f, If(r.Style = 2, Color.Firebrick, Ink), boxX + boxW * 0.45F, ry + 3.5F, boxW * 0.55F - 5, StringAlignment.Far)
                    If r.Style <> 1 Then HLine(g, Hairline, boxX, boxX + boxW, ry + h)
                End If
                ry += h
            Next

            Dim ny = y + 2, noteW = w - boxW - 24
            For Each n In Notes
                ny += Put(g, draw, n.Text, If(n.Bold, _noteB, _note), If(n.Bold, Ink, Muted), x, ny, noteW) + 3
            Next
            Return Math.Max(ry, ny) - top + 12
        End Function
    End Class

    ''' Signature boxes: title, then Name / Signature / Date lines per column.
    Private Class SignatureBlock
        Inherits Block
        Public Titles As New List(Of String)
        Private ReadOnly _title As New Font(Face, 8.5F, FontStyle.Bold)
        Private ReadOnly _k As New Font(Face, 8)
        Private Const GapX As Single = 22

        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            Dim n = Math.Max(1, Titles.Count)
            Dim pw = (w - GapX * (n - 1)) / n
            Dim h As Single = 0
            For i = 0 To Titles.Count - 1
                Dim px = x + i * (pw + GapX), py = y
                py += Put(g, draw, Titles(i).ToUpperInvariant(), _title, Accent, px, py, pw) + 14
                For Each lbl In {"Name", "Signature", "Date"}
                    Dim lh = Put(g, draw, lbl, _k, Muted, px, py, 56)
                    If draw Then HLine(g, Color.FromArgb(150, 150, 160), px + 58, px + pw, py + lh - 1)
                    py += lh + 16
                Next
                h = Math.Max(h, py - y)
            Next
            Return h + 4
        End Function
    End Class

    ''' A waybill's dispatch row: our own column carries only a Name line plus
    ''' the business's pre-signed "confirmed and released" stamp — no blank
    ''' Signature/Date for someone to fill in by hand, since the stamp already
    ''' carries both. The remaining columns (driver, receiving customer) are
    ''' still ordinary blank Name/Signature/Date lines, exactly as
    ''' SignatureBlock draws them, since those are signed by someone outside
    ''' the business.
    Private Class DispatchBlock
        Inherits Block
        Public StampImg As Image
        Public StampTitle As String = "Dispatched by"
        Public OtherTitles As New List(Of String)
        Private ReadOnly _title As New Font(Face, 8.5F, FontStyle.Bold)
        Private ReadOnly _k As New Font(Face, 8)
        Private Const GapX As Single = 22
        Private Const StampMaxH As Single = 70

        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            Dim n = 1 + Math.Max(1, OtherTitles.Count)
            Dim pw = (w - GapX * (n - 1)) / n
            Dim h As Single = 0

            Dim px = x, py = y
            py += Put(g, draw, StampTitle.ToUpperInvariant(), _title, Accent, px, py, pw) + 14
            Dim lh = Put(g, draw, "Name", _k, Muted, px, py, 56)
            If draw Then HLine(g, Color.FromArgb(150, 150, 160), px + 58, px + pw, py + lh - 1)
            py += lh + 16
            If StampImg IsNot Nothing Then
                Dim s = Math.Min(StampMaxH / StampImg.Height, pw / StampImg.Width)
                Dim iw = StampImg.Width * s, ih = StampImg.Height * s
                If draw Then g.DrawImage(StampImg, px, py, iw, ih)
                py += ih + 4
            End If
            h = Math.Max(h, py - y)

            For i = 0 To OtherTitles.Count - 1
                Dim cx = x + (i + 1) * (pw + GapX), cy = y
                cy += Put(g, draw, OtherTitles(i).ToUpperInvariant(), _title, Accent, cx, cy, pw) + 14
                For Each lbl In {"Name", "Signature", "Date"}
                    Dim clh = Put(g, draw, lbl, _k, Muted, cx, cy, 56)
                    If draw Then HLine(g, Color.FromArgb(150, 150, 160), cx + 58, cx + pw, cy + clh - 1)
                    cy += clh + 16
                Next
                h = Math.Max(h, cy - y)
            Next
            Return h + 4
        End Function
    End Class

    ''' Company stamp + signature, right-aligned, over an "Authorised signature" caption.
    Private Class StampBlock
        Inherits Block
        Public Img As Image
        Public Caption As String = "Authorised signature & company stamp"
        Public MaxHeight As Single = 108
        Private ReadOnly _cap As New Font(Face, 8)

        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            If Img Is Nothing Then Return 0
            Dim s = Math.Min(MaxHeight / Img.Height, (w * 0.4F) / Img.Width)
            Dim iw = Img.Width * s, ih = Img.Height * s
            Dim boxW = Math.Max(iw, w * 0.34F), boxX = x + w - boxW
            If draw Then g.DrawImage(Img, boxX + (boxW - iw) / 2, y, iw, ih)
            Dim ly = y + ih + 4
            If draw Then HLine(g, Color.FromArgb(150, 150, 160), boxX, boxX + boxW, ly)
            Dim ch = Put(g, draw, Caption, _cap, Muted, boxX, ly + 3, boxW, StringAlignment.Center)
            Return ih + 4 + 3 + ch + 8
        End Function
    End Class

    ''' Closing footer: small logo beside a thank-you line, centred as a group.
    Private Class BrandFooterBlock
        Inherits Block
        Public Logo As Image
        Public Message As String
        Private ReadOnly _msg As New Font(Face, 11, FontStyle.Bold)
        Private Const LogoH As Single = 34

        Public Overrides Function Render(g As Graphics, x As Single, y As Single, w As Single, draw As Boolean) As Single
            Dim top = y + 8
            If draw Then HLine(g, Hairline, x, x + w, y + 2)
            Dim lw As Single = 0
            If Logo IsNot Nothing Then lw = Logo.Width * (LogoH / Logo.Height)
            Dim tw = g.MeasureString(Message, _msg).Width
            Dim gapX As Single = If(lw > 0, 10, 0)
            Dim groupW = lw + gapX + tw
            Dim gx = x + (w - groupW) / 2
            Dim th = _msg.GetHeight(g)
            If draw Then
                If Logo IsNot Nothing Then g.DrawImage(Logo, gx, top, lw, LogoH)
                Using b As New SolidBrush(Accent)
                    g.DrawString(Message, _msg, b, gx + lw + gapX, top + (LogoH - th) / 2)
                End Using
            End If
            Return 8 + Math.Max(LogoH, th) + 6
        End Function
    End Class

    ' ===== builder API =====

    Private ReadOnly _blocks As New List(Of Block)
    Public Property DocTitle As String = "Document"
    Public Property Landscape As Boolean = False
    ''' Scale the whole document down (never below 35%) so it fits one page.
    Public Property FitToOnePage As Boolean = False
    Public Property FooterText As String

    Public Function Logo(Optional maxHeight As Single = 72) As DocPrinter
        If Theme.Logo IsNot Nothing Then _blocks.Add(New ImageBlock With {.Img = Theme.Logo, .MaxHeight = maxHeight})
        Return Me
    End Function

    ''' Standard letterhead: logo + company identity left; `title` and the
    ''' reference rows (e.g. Invoice no. / Date) right-aligned.
    Public Function Letterhead(title As String, meta As IEnumerable(Of (K As String, V As String))) As DocPrinter
        Dim b As New LetterheadBlock With {.Logo = Theme.Logo, .Company = AppInfo.CompanyName, .Title = title}
        If AppInfo.CompanyAddress <> "" Then b.CompanyLines.Add(AppInfo.CompanyAddress)
        Dim contact = String.Join("   ·   ", {AppInfo.CompanyPhone, AppInfo.CompanyEmail}.Where(Function(s) s <> ""))
        If contact <> "" Then b.CompanyLines.Add(contact)
        If AppInfo.CompanyTaxID <> "" Then b.CompanyLines.Add("Tax ID: " & AppInfo.CompanyTaxID)
        b.Meta.AddRange(meta.Where(Function(m) Not String.IsNullOrWhiteSpace(m.V)))
        _blocks.Add(b)
        Return Me
    End Function

    Public Function Panels(ParamArray items As (Title As String, Rows As (K As String, V As String, Bold As Boolean)())()) As DocPrinter
        Dim b As New PanelsBlock()
        For Each p In items
            b.Panels.Add((p.Title, p.Rows.ToList()))
        Next
        _blocks.Add(b)
        Return Me
    End Function

    Public Function SectionTitle(text As String) As DocPrinter
        _blocks.Add(New TextBlock With {.Text = text.ToUpperInvariant(), .Font = New Font(Face, 8.5F, FontStyle.Bold), .Colour = Accent, .SpacingAfter = 3})
        Return Me
    End Function

    Public Function Heading(text As String) As DocPrinter
        _blocks.Add(New TextBlock With {.Text = text, .Font = New Font(Face, 18, FontStyle.Bold), .SpacingAfter = 2})
        Return Me
    End Function
    Public Function SubHeading(text As String) As DocPrinter
        _blocks.Add(New TextBlock With {.Text = text, .Font = New Font(Face, 12, FontStyle.Bold), .SpacingAfter = 6})
        Return Me
    End Function
    Public Function Text(t As String, Optional bold As Boolean = False, Optional size As Single = 9.5F, Optional grey As Boolean = False) As DocPrinter
        _blocks.Add(New TextBlock With {
            .Text = t, .Font = New Font(Face, size, If(bold, FontStyle.Bold, FontStyle.Regular)),
            .Colour = If(grey, Muted, Ink)})
        Return Me
    End Function
    Public Function Centered(t As String, Optional bold As Boolean = False, Optional size As Single = 9.5F, Optional grey As Boolean = False) As DocPrinter
        _blocks.Add(New TextBlock With {.Text = t, .Font = New Font(Face, size, If(bold, FontStyle.Bold, FontStyle.Regular)),
                                        .Align = StringAlignment.Center, .Colour = If(grey, Muted, Ink)})
        Return Me
    End Function
    ''' A scannable symbol centred on the page, with its text under it so the
    ''' number can still be read out or typed when a scanner isn't to hand.
    ''' Takes ownership of the bitmap and disposes it with the document.
    Public Function Barcode(image As Image, Optional caption As String = Nothing) As DocPrinter
        _blocks.Add(New BarcodeBlock With {
            .Symbol = image, .Caption = caption,
            .CaptionFont = New Font(Face, 8), .CaptionColour = Muted})
        Return Me
    End Function

    Public Function Rule(Optional accentLine As Boolean = False) As DocPrinter
        _blocks.Add(New RuleBlock With {.Colour = If(accentLine, Accent, Hairline), .Thickness = If(accentLine, 1.5F, 1)})
        Return Me
    End Function
    Public Function Gap(Optional h As Single = 12) As DocPrinter
        _blocks.Add(New GapBlock With {.Height = h})
        Return Me
    End Function
    Public Function KeyValues(pairs As IEnumerable(Of (K As String, V As String, Bold As Boolean)), Optional labelWidth As Single = 150) As DocPrinter
        _blocks.Add(New KeyValueBlock With {.Pairs = pairs.ToList(), .LabelWidth = labelWidth})
        Return Me
    End Function
    ''' Numeric columns right-align automatically; `rightAlignFrom` forces columns
    ''' from that index onward to right-align (for pre-formatted text columns).
    Public Function Table(dt As DataTable, Optional weights As Single() = Nothing, Optional rightAlignFrom As Integer = -1,
                          Optional footer As String() = Nothing, Optional fontSize As Single = 9) As DocPrinter
        _blocks.Add(New TableBlock With {.Table = dt, .Weights = weights, .RightAlignFrom = rightAlignFrom, .Footer = footer, .FontSize = fontSize})
        Return Me
    End Function
    Public Function Totals(rows As IEnumerable(Of (K As String, V As String, Style As Integer)),
                           Optional notes As IEnumerable(Of (Text As String, Bold As Boolean)) = Nothing) As DocPrinter
        Dim b As New TotalsBlock()
        b.Rows.AddRange(rows)
        If notes IsNot Nothing Then b.Notes.AddRange(notes.Where(Function(n) Not String.IsNullOrWhiteSpace(n.Text)))
        _blocks.Add(b)
        Return Me
    End Function
    ''' The company stamp + signature from Assets\signature.png (skipped if missing).
    Public Function Stamp(Optional caption As String = "Authorised signature & company stamp") As DocPrinter
        If Theme.Signature IsNot Nothing Then _blocks.Add(New StampBlock With {.Img = Theme.Signature, .Caption = caption})
        Return Me
    End Function
    Public Function BrandFooter(Optional message As String = "Thanks for your patronage.") As DocPrinter
        _blocks.Add(New BrandFooterBlock With {.Logo = Theme.Logo, .Message = message})
        Return Me
    End Function
    Public Function Signatures(ParamArray titles As String()) As DocPrinter
        Dim b As New SignatureBlock()
        b.Titles.AddRange(titles)
        _blocks.Add(b)
        Return Me
    End Function

    ''' A waybill's dispatch row: our own column (stampImage) gets a Name line
    ''' plus the business's own pre-signed stamp; `otherTitles` (driver,
    ''' receiving customer) get ordinary blank Name/Signature/Date lines.
    Public Function DispatchSignatures(stampImage As Image, ParamArray otherTitles As String()) As DocPrinter
        _blocks.Add(New DispatchBlock With {.StampImg = stampImage, .OtherTitles = otherTitles.ToList()})
        Return Me
    End Function

    ' ===== rendering =====

    Private Const PdfPrinter As String = "Microsoft Print to PDF"
    Private Const MinScale As Single = 0.35F
    Private ReadOnly _footer As New Font(Face, 7.5F)

    Private Function ContentHeight(g As Graphics, w As Single) As Single
        Dim h As Single = 0
        For Each b In _blocks
            h += b.Render(g, 0, h, w, False)
        Next
        Return h
    End Function

    ''' Largest scale (≤ 1) at which the whole content fits the page — so text
    ''' stays as big as the page allows. Layout width grows as the scale drops
    ''' (less wrapping), so binary-search the scale rather than divide once.
    Private Function FitScale(g As Graphics, pageW As Single, pageH As Single) As Single
        Dim fits = Function(s As Single) As Boolean
                       Dim st = g.Save()
                       g.ScaleTransform(s, s)
                       Dim h = ContentHeight(g, pageW / s)
                       g.Restore(st)
                       Return h * s <= pageH * 0.995F
                   End Function
        If fits(1) Then Return 1
        Dim lo = MinScale, hi As Single = 1
        For i = 1 To 12
            Dim mid = (lo + hi) / 2
            If fits(mid) Then lo = mid Else hi = mid
        Next
        Return lo
    End Function

    Private Function BuildDocument(Optional printerName As String = Nothing) As PrintDocument
        Dim doc As New PrintDocument()
        doc.DocumentName = DocTitle
        If printerName IsNot Nothing Then doc.PrinterSettings.PrinterName = printerName
        ' A4 wherever the printer offers it (Nigeria / most of the world).
        Try
            For Each ps As PaperSize In doc.PrinterSettings.PaperSizes
                If ps.Kind = PaperKind.A4 Then
                    doc.DefaultPageSettings.PaperSize = ps
                    Exit For
                End If
            Next
        Catch
        End Try
        doc.DefaultPageSettings.Landscape = Landscape
        doc.DefaultPageSettings.Margins = New Margins(55, 55, 50, 60)

        Dim index = 0, pageNo = 0
        AddHandler doc.BeginPrint, Sub(s, e)
                                       index = 0
                                       pageNo = 0
                                       For Each b In _blocks
                                           b.Reset()
                                       Next
                                   End Sub
        AddHandler doc.PrintPage,
            Sub(s, e)
                Dim g = e.Graphics
                g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit
                g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
                Dim m = e.MarginBounds
                pageNo += 1
                Dim foot = If(String.IsNullOrEmpty(FooterText), DocTitle, FooterText)
                g.DrawString(If(FitToOnePage, foot, $"{foot}   ·   page {pageNo}"), _footer, Brushes.Gray, m.Left, m.Bottom + 20)

                If FitToOnePage Then
                    Dim sc = FitScale(g, m.Width, m.Height)
                    Dim st = g.Save()
                    g.TranslateTransform(m.Left, m.Top)
                    g.ScaleTransform(sc, sc)
                    Dim y As Single = 0
                    For Each b In _blocks
                        y += b.Render(g, 0, y, m.Width / sc, True)
                    Next
                    g.Restore(st)
                    e.HasMorePages = False
                    Return
                End If

                Dim x As Single = m.Left, yy As Single = m.Top, w As Single = m.Width, bottom As Single = m.Bottom
                While index < _blocks.Count
                    Dim b = _blocks(index)
                    If yy + b.StartHeight(g, w) > bottom AndAlso yy > m.Top Then
                        e.HasMorePages = True
                        Return
                    End If
                    If Not b.DrawPaged(g, x, yy, w, bottom) Then
                        e.HasMorePages = True
                        Return
                    End If
                    index += 1
                End While
                e.HasMorePages = False
            End Sub
        Return doc
    End Function

    ''' How many pages this document comes to (used by the viewer and the tests).
    Public Function PageCount() As Integer
        Return RenderPages().Count
    End Function

    ''' Renders every page to an image (for the on-screen viewer).
    Friend Function RenderPages() As List(Of PreviewPageInfo)
        Dim doc = BuildDocument()
        Dim pc As New PreviewPrintController() With {.UseAntiAlias = True}
        doc.PrintController = pc
        Try
            doc.Print()
        Catch ex As InvalidPrinterException
            ' No default printer — render against the built-in PDF printer instead.
            doc = BuildDocument(PdfPrinter)
            doc.PrintController = pc
            doc.Print()
        End Try
        Return pc.GetPreviewPageInfo().ToList()
    End Function

    ''' Straight to a .pdf file via Windows' built-in "Microsoft Print to PDF".
    ''' Returns the saved path, or Nothing if cancelled / unavailable.
    Public Function SaveAsPdf(owner As IWin32Window) As String
        If Not PdfPrinterAvailable() Then
            AppUI.Info(owner, "Windows' ""Microsoft Print to PDF"" printer isn't installed on this PC. Use Print… and choose any PDF printer instead.", "Save as PDF")
            Return Nothing
        End If
        Using sfd As New SaveFileDialog() With {.Filter = "PDF document (*.pdf)|*.pdf", .FileName = SafeFileName(DocTitle) & ".pdf", .Title = "Save as PDF"}
            If sfd.ShowDialog(owner) <> DialogResult.OK Then Return Nothing
            Dim err = SaveAsPdfTo(sfd.FileName)
            If err <> "" Then
                AppUI.Toast("Couldn't save the PDF: " & err, AppUI.ToastKind.Error)
                Return Nothing
            End If
            AppUI.Toast("Saved " & Path.GetFileName(sfd.FileName), AppUI.ToastKind.Success)
            Return sfd.FileName
        End Using
    End Function

    ''' Writes the document straight to `path` as PDF (no dialogs).
    ''' Returns "" on success, else the error text.
    Public Function SaveAsPdfTo(path As String) As String
        If Not PdfPrinterAvailable() Then Return PdfPrinter & " is not installed on this PC."
        Try
            Dim doc = BuildDocument(PdfPrinter)
            doc.PrinterSettings.PrintToFile = True
            doc.PrinterSettings.PrintFileName = path
            doc.PrintController = New StandardPrintController()
            doc.Print()
            Return ""
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

    Public Shared Function PdfPrinterAvailable() As Boolean
        Return PrinterSettings.InstalledPrinters.Cast(Of String)().Contains(PdfPrinter)
    End Function

    Private Shared Function SafeFileName(name As String) As String
        For Each ch In Path.GetInvalidFileNameChars()
            name = name.Replace(ch, "_"c)
        Next
        Return name.Replace("—", "-").Trim()
    End Function

    ''' Printer dialog (no preview).
    Public Sub PrintDirect(owner As IWin32Window)
        Dim doc = BuildDocument()
        Using pd As New PrintDialog() With {.Document = doc, .UseEXDialog = True}
            If pd.ShowDialog(owner) = DialogResult.OK Then
                Try
                    doc.Print()
                Catch ex As Exception
                    AppUI.Toast("Print failed: " & ex.Message, AppUI.ToastKind.Error)
                End Try
            End If
        End Using
    End Sub

    ''' The on-screen document viewer (toolbar + scrollable pages) as a control
    ''' to drop into any form or tab.
    Public Function CreateViewer() As Control
        Return New DocViewer(Me)
    End Function

    ''' Viewer in its own window, sized to the screen.
    Public Sub ShowPreview(owner As IWin32Window)
        Dim wa = Screen.FromPoint(Cursor.Position).WorkingArea
        Using f As New Form() With {
            .Text = DocTitle, .StartPosition = FormStartPosition.CenterScreen,
            .Width = Math.Min(1100, wa.Width - 40), .Height = wa.Height - 40, .MinimizeBox = False}
            If Theme.AppIcon IsNot Nothing Then f.Icon = Theme.AppIcon
            Dim v = CreateViewer()
            v.Dock = DockStyle.Fill
            f.Controls.Add(v)
            f.ShowDialog(owner)
        End Using
    End Sub

    ' ===== viewer =====

    Private NotInheritable Class DocViewer
        Inherits UserControl

        Private ReadOnly _printer As DocPrinter
        Private ReadOnly _canvas As New PageCanvas()
        Private ReadOnly lblZoom As New Label() With {.AutoSize = True, .Tag = "keepfont", .Margin = New Padding(12, 9, 0, 0)}
        Private _fitWidth As Boolean = True

        Public Sub New(printer As DocPrinter)
            _printer = printer
            Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8, 6, 8, 6), .WrapContents = True}
            Dim btnPdf As New Button() With {.Text = "Save as PDF…", .AutoSize = True, .Tag = "primary"}
            Dim btnPrint As New Button() With {.Text = "Print…", .AutoSize = True}
            Dim btnOut As New Button() With {.Text = "−", .Width = 36}
            Dim btnIn As New Button() With {.Text = "+", .Width = 36}
            Dim btnFit As New Button() With {.Text = "Fit width", .AutoSize = True}
            Dim btn100 As New Button() With {.Text = "100%", .AutoSize = True}
            Theme.StylePrimaryButton(btnPdf)
            bar.Controls.AddRange(New Control() {btnPdf, btnPrint, New Label() With {.Width = 16}, btnOut, btnFit, btn100, btnIn, lblZoom})

            AddHandler btnPdf.Click, Sub(s, e) _printer.SaveAsPdf(FindForm())
            AddHandler btnPrint.Click, Sub(s, e) _printer.PrintDirect(FindForm())
            AddHandler btnFit.Click, Sub(s, e) SetFit()
            AddHandler btn100.Click, Sub(s, e) SetZoom(1.0F)
            AddHandler btnIn.Click, Sub(s, e) SetZoom(_canvas.Zoom * 1.2F)
            AddHandler btnOut.Click, Sub(s, e) SetZoom(_canvas.Zoom / 1.2F)
            AddHandler _canvas.ZoomRequested, Sub(factor) SetZoom(_canvas.Zoom * factor)
            AddHandler _canvas.ClientSizeChanged, Sub(s, e) If _fitWidth Then ApplyFit()

            _canvas.Dock = DockStyle.Fill
            Controls.Add(_canvas)   ' Fill first so the toolbar keeps its strip
            Controls.Add(bar)

            Try
                _canvas.Pages = _printer.RenderPages()
            Catch ex As Exception
                _canvas.ErrorText = "Couldn't render this document: " & ex.Message
            End Try
            AddHandler Load, Sub(s, e) SetFit()
        End Sub

        Private Sub SetFit()
            _fitWidth = True
            ApplyFit()
        End Sub

        Private Sub ApplyFit()
            _canvas.Zoom = _canvas.FitWidthZoom()
            _canvas.Relayout()
            ShowZoom()
        End Sub

        Private Sub SetZoom(z As Single)
            _fitWidth = False
            _canvas.Zoom = Math.Max(0.25F, Math.Min(4.0F, z))
            _canvas.Relayout()
            ShowZoom()
        End Sub

        Private Sub ShowZoom()
            Dim n = If(_canvas.Pages Is Nothing, 0, _canvas.Pages.Count)
            lblZoom.Text = $"{n} page{If(n = 1, "", "s")}   ·   {_canvas.Zoom * 100:0}%   ·   Ctrl + mouse wheel to zoom"
        End Sub
    End Class

    ''' Paints the rendered pages stacked vertically, centred, with scrollbars.
    Private NotInheritable Class PageCanvas
        Inherits Panel

        Public Pages As List(Of PreviewPageInfo)
        Public ErrorText As String
        Public Zoom As Single = 1.0F   ' 1.0 = actual size on screen
        Public Event ZoomRequested(factor As Single)
        Private Const Gap As Integer = 18

        Public Sub New()
            DoubleBuffered = True
            AutoScroll = True
            SetStyle(ControlStyles.Selectable, True)   ' so it takes focus for mouse-wheel scrolling
            ResizeRedraw = True
        End Sub

        ' PreviewPageInfo.PhysicalSize is in 1/100 inch.
        Private Function PxPerUnit() As Single
            Using g = CreateGraphics()
                Return g.DpiX / 100.0F * Zoom
            End Using
        End Function

        Public Function FitWidthZoom() As Single
            If Pages Is Nothing OrElse Pages.Count = 0 Then Return 1.0F
            Dim maxW = Pages.Max(Function(p) p.PhysicalSize.Width)
            Using g = CreateGraphics()
                Dim avail = ClientSize.Width - Gap * 2 - SystemInformation.VerticalScrollBarWidth
                Return Math.Max(0.25F, Math.Min(3.0F, avail / (maxW * g.DpiX / 100.0F)))
            End Using
        End Function

        Public Sub Relayout()
            If Pages Is Nothing OrElse Pages.Count = 0 Then
                AutoScrollMinSize = Size.Empty
                Invalidate()
                Return
            End If
            Dim k = PxPerUnit()
            Dim w = CInt(Pages.Max(Function(p) p.PhysicalSize.Width) * k) + Gap * 2
            Dim h = CInt(Pages.Sum(Function(p) p.PhysicalSize.Height * k)) + Gap * (Pages.Count + 1)
            AutoScrollMinSize = New Size(w, h)
            Invalidate()
        End Sub

        ' Own background colour — theming repaints panels, but the page desk stays grey.
        Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
            e.Graphics.Clear(Color.FromArgb(128, 128, 134))
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            MyBase.OnPaint(e)
            Dim g = e.Graphics
            If Not String.IsNullOrEmpty(ErrorText) Then
                g.DrawString(ErrorText, Font, Brushes.White, 20, 20)
                Return
            End If
            If Pages Is Nothing Then Return
            g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic
            Dim k = PxPerUnit()
            Dim areaW = Math.Max(ClientSize.Width, AutoScrollMinSize.Width)
            Dim y = Gap + AutoScrollPosition.Y
            For Each p In Pages
                Dim pw = CInt(p.PhysicalSize.Width * k), ph = CInt(p.PhysicalSize.Height * k)
                Dim x = (areaW - pw) \ 2 + AutoScrollPosition.X
                If y + ph >= 0 AndAlso y <= ClientSize.Height Then
                    g.FillRectangle(Brushes.DimGray, x + 4, y + 4, pw, ph)
                    g.FillRectangle(Brushes.White, x, y, pw, ph)
                    g.DrawImage(p.Image, x, y, pw, ph)
                End If
                y += ph + Gap
            Next
        End Sub

        Protected Overrides Sub OnMouseEnter(e As EventArgs)
            MyBase.OnMouseEnter(e)
            Focus()
        End Sub

        Protected Overrides Sub OnMouseWheel(e As MouseEventArgs)
            If (ModifierKeys And Keys.Control) = Keys.Control Then
                RaiseEvent ZoomRequested(If(e.Delta > 0, 1.15F, 1 / 1.15F))
                Return
            End If
            MyBase.OnMouseWheel(e)
        End Sub
    End Class

End Class
