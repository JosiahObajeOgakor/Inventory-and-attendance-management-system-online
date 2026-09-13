Imports System.Windows.Forms
Imports System.Drawing

''' Small helpers so every "Add X" dialog isn't 80 lines of manual control
''' positioning — a 2-column TableLayoutPanel (label | input) that grows
''' downward one AddLabeled() call per field.
Public Module UiHelpers

    Public Function NewFormTable() As TableLayoutPanel
        Dim t As New TableLayoutPanel()
        t.Dock = DockStyle.Top
        t.AutoSize = True
        t.ColumnCount = 2
        t.Padding = New Padding(16)
        t.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 130))
        t.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Return t
    End Function

    Public Function AddLabeled(table As TableLayoutPanel, labelText As String, control As Control) As Control
        Dim row = table.RowCount
        table.RowCount += 1
        table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Dim lbl As New Label()
        lbl.Text = labelText
        lbl.AutoSize = True
        lbl.Anchor = AnchorStyles.Left
        lbl.Margin = New Padding(0, 8, 10, 4)
        control.Dock = DockStyle.Fill
        control.Margin = New Padding(0, 4, 0, 4)
        table.Controls.Add(lbl, 0, row)
        table.Controls.Add(control, 1, row)
        Return control
    End Function

    Private Const OkRowName As String = "okCancelRow"

    ''' Call at the end of a dialog's constructor: moves everything except the
    ''' OK/Cancel row into a scrolling body, so on a small or high-DPI screen the
    ''' fields scroll while Save/Cancel stay pinned and visible at the bottom.
    Public Sub MakeScrollable(form As Form)
        form.SuspendLayout()
        Dim body As New Panel() With {.Dock = DockStyle.Fill, .AutoScroll = True}
        Dim movers = form.Controls.Cast(Of Control)().Where(Function(c) c.Name <> OkRowName).ToList()
        For Each c In movers   ' same relative order → same docking layout inside the body
            form.Controls.Remove(c)
            body.Controls.Add(c)
        Next
        form.Controls.Add(body)
        body.BringToFront()   ' Fill docks last, so the OK row keeps its strip
        FitToScreen(form)
        form.ResumeLayout()
    End Sub

    ''' Keeps a window inside the visible screen area.
    Public Sub FitToScreen(form As Form)
        Dim wa = Screen.FromPoint(Cursor.Position).WorkingArea
        If form.Height > wa.Height - 20 Then form.Height = wa.Height - 20
        If form.Width > wa.Width - 20 Then form.Width = wa.Width - 20
    End Sub

    ''' Standard OK/Cancel row used by every dialog — wires DialogResult and Close.
    Public Function AddOkCancelRow(form As Form, okText As String, okHandler As EventHandler) As FlowLayoutPanel
        Dim row As New FlowLayoutPanel()
        row.Name = OkRowName
        row.Dock = DockStyle.Bottom
        row.FlowDirection = FlowDirection.RightToLeft
        row.AutoSize = True
        row.Padding = New Padding(16)

        Dim btnCancel As New Button()
        btnCancel.Text = "Cancel"
        btnCancel.AutoSize = True
        AddHandler btnCancel.Click, Sub(s, e)
                                        form.DialogResult = DialogResult.Cancel
                                        form.Close()
                                    End Sub

        Dim btnOk As New Button()
        btnOk.Text = okText
        btnOk.Tag = "primary"
        AddHandler btnOk.Click, okHandler
        Theme.StylePrimaryButton(btnOk)

        row.Controls.Add(btnCancel)
        row.Controls.Add(btnOk)
        form.Controls.Add(row)
        Return row
    End Function

    ''' A themed KPI/metric card (muted uppercase title over a large bold value).
    ''' Shared by the Dashboard and Finance screens. Drop the returned control
    ''' straight into a FlowLayoutPanel. Labels are tagged "keepfont" so the
    ''' global theme font pass leaves their sizes alone.
    Public Function MetricCard(title As String, value As String, Optional accent As Color? = Nothing) As Control
        Dim inner As New TableLayoutPanel() With {
            .ColumnCount = 1, .RowCount = 2, .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Margin = New Padding(0),
            .Padding = New Padding(16, 12, 22, 14), .BackColor = Theme.Current.Surface
        }
        inner.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        inner.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        inner.Controls.Add(New Label() With {
            .Text = title.ToUpperInvariant(), .AutoSize = True, .Tag = "keepfont",
            .ForeColor = Theme.Current.TextMuted, .Margin = New Padding(0, 0, 0, 6),
            .Font = New Font("Segoe UI", Math.Max(8, Theme.BaseFontSize - 3), FontStyle.Regular)
        }, 0, 0)
        inner.Controls.Add(New Label() With {
            .Text = value, .AutoSize = True, .Tag = "keepfont",
            .ForeColor = If(accent.HasValue, accent.Value, Theme.Current.TextPrimary),
            .Margin = New Padding(0),
            .Font = New Font("Segoe UI", Theme.BaseFontSize + 5, FontStyle.Bold)
        }, 0, 1)

        ' Hairline border + subtle top accent stripe.
        Dim frame As New TableLayoutPanel() With {
            .ColumnCount = 1, .RowCount = 2, .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Margin = New Padding(0, 0, 14, 12),
            .Padding = New Padding(1), .BackColor = Theme.Current.GridLineColor
        }
        frame.RowStyles.Add(New RowStyle(SizeType.Absolute, 3))
        frame.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        frame.Controls.Add(New Panel() With {.Height = 3, .Dock = DockStyle.Fill,
            .BackColor = If(accent.HasValue, accent.Value, Theme.Current.Primary)}, 0, 0)
        frame.Controls.Add(inner, 0, 1)
        Return frame
    End Function

    ''' A language dropdown wired to Lang. `onChange` runs after the switch so the
    ''' caller can re-apply Theme (which re-translates) to its form.
    Public Function LanguagePicker(Optional onChange As Action = Nothing) As ComboBox
        Dim cbo As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 130}
        cbo.Items.AddRange(Lang.Available.Select(Function(l) CObj(l.Name)).ToArray())
        Dim idx = Array.FindIndex(Lang.Available, Function(l) l.Code = Lang.CurrentCode)
        cbo.SelectedIndex = Math.Max(0, idx)
        AddHandler cbo.SelectedIndexChanged, Sub(s, e)
                                                 Lang.SetLanguage(Lang.Available(cbo.SelectedIndex).Code)
                                                 onChange?.Invoke()
                                             End Sub
        Return cbo
    End Function

    ''' Consistent, accountant-style columns everywhere: money right-aligned with
    ''' thousands separators and 2 decimals, counts right-aligned, dates as
    ''' "09 Sep 2026", and "TotalAmount"-style names shown as "Total amount".
    ''' Only display formatting — cell values (used by code) are untouched.
    Private Sub FormatColumns(g As DataGridView)
        Dim countWords = {"qty", "quantity", "onhand", "stock", "units", "lines", "invoices", "count", "level", "waybills"}
        For Each c As DataGridViewColumn In g.Columns
            If c.HeaderText = c.Name Then c.HeaderText = FriendlyHeader(c.Name)
            Dim t = c.ValueType
            If t Is Nothing Then Continue For
            If t Is GetType(Decimal) OrElse t Is GetType(Double) OrElse t Is GetType(Single) Then
                c.DefaultCellStyle.Format = "N2"
                RightAlign(c)
            ElseIf t Is GetType(Integer) OrElse t Is GetType(Long) OrElse t Is GetType(Short) Then
                Dim n = c.Name.ToLowerInvariant()
                If countWords.Any(Function(wd) n.Contains(wd)) Then c.DefaultCellStyle.Format = "#,0"
                RightAlign(c)
            ElseIf t Is GetType(DateTime) Then
                Dim hasTime = False
                For i = 0 To Math.Min(g.Rows.Count, 20) - 1
                    Dim v = g.Rows(i).Cells(c.Index).Value
                    If TypeOf v Is DateTime AndAlso DirectCast(v, DateTime).TimeOfDay <> TimeSpan.Zero Then hasTime = True : Exit For
                Next
                c.DefaultCellStyle.Format = If(hasTime, "dd MMM yyyy HH:mm", "dd MMM yyyy")
            End If
        Next
    End Sub

    Private Sub RightAlign(c As DataGridViewColumn)
        c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight
        c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight
    End Sub

    ''' "TotalAmount" → "Total amount", "VATRate" → "VAT rate", "InvoiceID" → "Invoice ID".
    Public Function FriendlyHeader(name As String) As String
        If name.Contains(" ") OrElse name.Length <= 3 Then Return name
        Dim words = System.Text.RegularExpressions.Regex.Matches(name, "[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+").
            Cast(Of System.Text.RegularExpressions.Match)().Select(Function(m) m.Value).ToList()
        If words.Count <= 1 Then Return name
        For i = 1 To words.Count - 1
            If words(i).Any(AddressOf Char.IsLower) Then words(i) = words(i).ToLowerInvariant()
        Next
        Return String.Join(" ", words)
    End Function

    ''' Every column gets the width its header/content actually needs. If that
    ''' total is narrower than the grid, the extra room is shared out so the table
    ''' still fills the space; if it's wider, columns keep their real width and
    ''' the grid's own horizontal scrollbar appears — a plain, honest signal that
    ''' there's more to the right, rather than the last column being sliced off
    ''' with no way to tell anything follows it.
    ' Setting column widths can add or remove the grid's own scrollbar, which
    ' resizes it, which asks for the widths again. Nested in a paged grid that
    ' loop never settles and takes the process down with it, so the fitting pass
    ' refuses to re-enter itself.
    Private _fittingColumns As Boolean

    Private Sub SetReadableColumnWidths(g As DataGridView)
        If _fittingColumns Then Return
        _fittingColumns = True
        Try
            FitColumns(g)
        Finally
            _fittingColumns = False
        End Try
    End Sub

    Private Sub FitColumns(g As DataGridView)
        FormatColumns(g)
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        Dim headFont = If(g.ColumnHeadersDefaultCellStyle.Font, g.Font)
        Dim sample = Math.Min(g.Rows.Count, 30)
        Dim visible = g.Columns.Cast(Of DataGridViewColumn)().Where(Function(c) c.Visible).ToList()
        If visible.Count = 0 Then Return

        Dim widths As New Dictionary(Of DataGridViewColumn, Integer)
        For Each c In visible
            Dim w = TextRenderer.MeasureText(c.HeaderText, headFont).Width + 34   ' + sort-glyph clearance
            For i = 0 To sample - 1
                Dim txt = Convert.ToString(g.Rows(i).Cells(c.Index).FormattedValue)
                w = Math.Max(w, TextRenderer.MeasureText(txt, g.Font).Width + 20)
            Next
            widths(c) = Math.Max(60, Math.Min(w, 280))
        Next

        Dim total = widths.Values.Sum()
        ' Don't reserve room for a vertical scrollbar that may never appear (most
        ' of these tables are short) — that reservation was leaving a bare strip
        ' down the right edge of narrow grids. A table long enough to need one
        ' gets it, and columns simply gain a matching horizontal scrollbar too.
        Dim avail = g.ClientSize.Width - 2
        If total < avail Then
            Dim extra = avail - total
            Dim share = extra \ visible.Count
            For Each c In visible
                c.Width = widths(c) + share
            Next
            visible.Last().Width += extra - share * visible.Count   ' remainder to the last column
        Else
            For Each c In visible
                c.Width = widths(c)
            Next
        End If
    End Sub

    Public Function NewGrid() As DataGridView
        Dim g As New DataGridView()
        g.Dock = DockStyle.Fill
        g.ReadOnly = True
        g.AllowUserToAddRows = False
        g.AllowUserToDeleteRows = False
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        Theme.ApplyGrid(g)
        AddHandler g.DataBindingComplete, Sub(s, e)
                                              ApplyRowNumbers(g)
                                              SetReadableColumnWidths(g)
                                          End Sub
        ' Column widths depend on the grid's own width — redo the split when it resizes.
        AddHandler g.Resize, Sub(s, e) If g.Columns.Count > 0 Then SetReadableColumnWidths(g)
        ' Re-apply palette/table-design whenever the user changes Appearance.
        AddHandler Theme.ThemeChanged, Sub(s, e)
                                          If Not g.IsDisposed Then
                                              Theme.ApplyGrid(g)
                                              ApplyRowNumbers(g)
                                          End If
                                      End Sub
        Return g
    End Function

    Private Sub ApplyRowNumbers(g As DataGridView)
        If Not Theme.ShowGridRowNumbers Then Return
        For i = 0 To g.Rows.Count - 1
            g.Rows(i).HeaderCell.Value = (i + 1).ToString()
        Next
    End Sub

End Module
