Imports System.Windows.Forms
Imports System.Drawing

''' Appearance settings — tune colour, readability, table density and visual
''' effects. Saves to AppSettings and restyles the open workspace immediately.
''' Saves to the AppSettings table (shared by the whole office) and raises
''' Theme.ThemeChanged so the open main window restyles immediately.
Public Class frmAppearance
    Inherits Form

    Private ReadOnly cboPalette As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 220}
    Private ReadOnly numFont As New NumericUpDown() With {.Minimum = 9, .Maximum = 22, .Width = 80}
    Private ReadOnly cboDensity As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 220}
    Private ReadOnly cboLines As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 220}
    Private ReadOnly chkStripes As New CheckBox() With {.Text = "Striped rows"}
    Private ReadOnly chkBoldHeaders As New CheckBox() With {.Text = "Bold column headers"}
    Private ReadOnly chkRowNumbers As New CheckBox() With {.Text = "Show row numbers in tables"}
    Private ReadOnly chkReducedEffects As New CheckBox() With {.Text = "Reduce card shadows"}
    Private ReadOnly preview As DataGridView = UiHelpers.NewGrid()
    Private ReadOnly swatch As New Panel() With {.Height = 34, .Dock = DockStyle.Fill, .Margin = New Padding(0, 4, 0, 4)}

    ' Originals, restored if the user cancels (the live preview mutates Theme).
    Private ReadOnly _origPalette As Theme.Palette = Theme.Current
    Private ReadOnly _origFont As Single = Theme.BaseFontSize
    Private ReadOnly _origDensity As Theme.TableDensity = Theme.Density
    Private ReadOnly _origLines As Theme.TableLines = Theme.Lines
    Private ReadOnly _origStripes As Boolean = Theme.RowStripes
    Private ReadOnly _origBold As Boolean = Theme.BoldHeaders
    Private ReadOnly _origRowNumbers As Boolean = Theme.ShowGridRowNumbers
    Private ReadOnly _origReducedEffects As Boolean = Theme.ReducedEffects

    Public Sub New()
        Text = Theme.AppName & " — Appearance"
        Width = 640
        Height = 560
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False

        cboPalette.Items.AddRange(Theme.Palettes.Select(Function(p) CObj(p.Name)).ToArray())
        cboPalette.SelectedItem = Theme.Current.Name
        numFont.Value = CDec(Math.Round(Theme.BaseFontSize))
        cboDensity.Items.AddRange({"Compact", "Comfortable", "Spacious"})
        cboDensity.SelectedIndex = CInt(Theme.Density)
        cboLines.Items.AddRange({"No grid lines", "Horizontal lines", "Full grid"})
        cboLines.SelectedIndex = CInt(Theme.Lines)
        chkStripes.Checked = Theme.RowStripes
        chkBoldHeaders.Checked = Theme.BoldHeaders
        chkRowNumbers.Checked = Theme.ShowGridRowNumbers
        chkReducedEffects.Checked = Theme.ReducedEffects

        Dim table = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(table, "Language", UiHelpers.LanguagePicker(Sub() Theme.Apply(Me)))
        UiHelpers.AddLabeled(table, "Colour palette", cboPalette)
        UiHelpers.AddLabeled(table, "Palette colours", swatch)
        UiHelpers.AddLabeled(table, "Base font size", numFont)
        UiHelpers.AddLabeled(table, "Row height", cboDensity)
        UiHelpers.AddLabeled(table, "Table lines", cboLines)
        UiHelpers.AddLabeled(table, "", chkStripes)
        UiHelpers.AddLabeled(table, "", chkBoldHeaders)
        UiHelpers.AddLabeled(table, "", chkRowNumbers)
        UiHelpers.AddLabeled(table, "Visual effects", chkReducedEffects)

        Dim previewHost As New Panel() With {.Dock = DockStyle.Fill, .Padding = New Padding(16, 8, 16, 8)}
        preview.Height = 180
        previewHost.Controls.Add(preview)
        previewHost.Controls.Add(New Label() With {.Text = "Preview", .Dock = DockStyle.Top, .AutoSize = True, .Tag = "heading"})

        Controls.Add(previewHost)
        Controls.Add(table)
        UiHelpers.AddOkCancelRow(Me, "Apply", AddressOf Apply_Click)

        For Each cb In {cboPalette, cboDensity, cboLines}
            AddHandler cb.SelectedIndexChanged, Sub(s, e) RefreshPreview()
        Next
        AddHandler numFont.ValueChanged, Sub(s, e) RefreshPreview()
        AddHandler chkStripes.CheckedChanged, Sub(s, e) RefreshPreview()
        AddHandler chkBoldHeaders.CheckedChanged, Sub(s, e) RefreshPreview()
        AddHandler chkRowNumbers.CheckedChanged, Sub(s, e) RefreshPreview()
        AddHandler chkReducedEffects.CheckedChanged, Sub(s, e) RefreshPreview()
        AddHandler Me.Load, Sub(s, e) RefreshPreview()
        AddHandler Me.FormClosing, Sub(s, e)
                                       If DialogResult <> DialogResult.OK Then
                                           Theme.Current = _origPalette
                                           Theme.BaseFontSize = _origFont
                                           Theme.Density = _origDensity
                                           Theme.Lines = _origLines
                                           Theme.RowStripes = _origStripes
                                           Theme.BoldHeaders = _origBold
                                           Theme.ShowGridRowNumbers = _origRowNumbers
                                           Theme.ReducedEffects = _origReducedEffects
                                           Theme.RaiseThemeChanged()
                                       End If
                                   End Sub
    End Sub

    ''' Snapshot the current dialog choices into Theme, refresh the preview grid,
    ''' then (on Apply) persist + broadcast. Cancel restores the originals.
    Private Sub PushToTheme()
        Theme.Current = Theme.Palettes.First(Function(p) p.Name = CStr(cboPalette.SelectedItem))
        Theme.BaseFontSize = CSng(numFont.Value)
        Theme.Density = CType(cboDensity.SelectedIndex, Theme.TableDensity)
        Theme.Lines = CType(cboLines.SelectedIndex, Theme.TableLines)
        Theme.RowStripes = chkStripes.Checked
        Theme.BoldHeaders = chkBoldHeaders.Checked
        Theme.ShowGridRowNumbers = chkRowNumbers.Checked
        Theme.ReducedEffects = chkReducedEffects.Checked
    End Sub

    Private Sub RefreshPreview()
        PushToTheme()

        swatch.Controls.Clear()
        For Each c In {Theme.Current.NavBg, Theme.Current.Primary, Theme.Current.Danger,
                       Theme.Current.GridHeaderBg, Theme.Current.GridAltRowBg, Theme.Current.GridSelectionBg}
            swatch.Controls.Add(New Panel() With {.Width = 34, .Height = 28, .BackColor = c, .Dock = DockStyle.Left, .Margin = New Padding(2)})
        Next

        Dim dt As New DataTable()
        dt.Columns.AddRange({New DataColumn("SKU"), New DataColumn("Item"), New DataColumn("Qty", GetType(Integer)), New DataColumn("Status")})
        dt.Rows.Add("SKU-1001", "Adult Dog Food 20kg", 42, "OK")
        dt.Rows.Add("SKU-1002", "Puppy Starter 10kg", 8, "Low stock")
        dt.Rows.Add("SKU-1003", "Cat Food Premium 5kg", 65, "OK")
        dt.Rows.Add("SKU-1004", "Senior Dog Formula 15kg", 9, "Expiring soon")
        preview.DataSource = dt
        Theme.ApplyGrid(preview)
        If Theme.ShowGridRowNumbers Then
            For i = 0 To preview.Rows.Count - 1
                preview.Rows(i).HeaderCell.Value = (i + 1).ToString()
            Next
        End If
        Theme.Apply(Me)
        previewHostInvalidate()
    End Sub

    Private Sub previewHostInvalidate()
        preview.Parent?.Invalidate(True)
    End Sub

    Private Sub Apply_Click(sender As Object, e As EventArgs)
        PushToTheme()
        Theme.Save()
        Theme.RaiseThemeChanged()
        DialogResult = DialogResult.OK
        Close()
    End Sub

End Class
