Imports System.Windows.Forms
Imports System.Drawing

''' Overrides one line's price on a sale or quotation. Never touches stock —
''' it only changes what a line is charged at. The override is recorded to
''' PriceOverrides (see Sales.SaveOnce / Quotations.SaveOnce) once the document
''' itself is saved, so every change here is traceable to whoever made it.
Public Class frmEditPrice
    Inherits Form

    Private ReadOnly numPrice As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private ReadOnly lblStandard As New Label() With {.AutoSize = True, .Tag = "keepfont", .ForeColor = Theme.Current.TextMuted}
    Private ReadOnly btnSave As New Button() With {.Text = "Use this price", .Tag = "primary", .AutoSize = True}
    Private ReadOnly btnCancel As New Button() With {.Text = "Cancel", .AutoSize = True}

    Public ReadOnly Property NewPrice As Decimal

    Public Sub New(productName As String, standardPrice As Decimal, currentPrice As Decimal)
        Text = "Edit price — " & productName
        Width = 400
        Height = 230
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        lblStandard.Text = "Standard price for this tier: " & AppInfo.Money2(standardPrice)
        lblStandard.Padding = New Padding(16, 16, 16, 4)
        lblStandard.Dock = DockStyle.Top

        numPrice.Value = currentPrice

        Dim table = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(table, "Price to charge", numPrice)

        Dim buttons As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(12)}
        buttons.Controls.Add(btnCancel)
        buttons.Controls.Add(btnSave)

        Controls.Add(table)
        Controls.Add(buttons)
        Controls.Add(lblStandard)

        AddHandler btnSave.Click, Sub(s, e)
                                       _NewPrice = numPrice.Value
                                       DialogResult = DialogResult.OK
                                       Close()
                                   End Sub
        AddHandler btnCancel.Click, Sub(s, e)
                                        DialogResult = DialogResult.Cancel
                                        Close()
                                    End Sub

        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

End Class
