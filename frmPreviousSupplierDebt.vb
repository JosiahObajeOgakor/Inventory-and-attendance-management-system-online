Imports System.Windows.Forms
Imports System.Drawing

''' Shown the moment a supplier we already owe is picked for a new purchase —
''' the mirror of frmPreviousDebt, but for what WE owe THEM. Offers to pay some
''' or all of it now, on top of this order; declining leaves it exactly where
''' it was, on the supplier's account.
Public Class frmPreviousSupplierDebt
    Inherits Form

    Private ReadOnly numAmount As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private ReadOnly lblInfo As New Label() With {
        .Dock = DockStyle.Top, .AutoSize = True, .Tag = "keepfont",
        .Padding = New Padding(16, 16, 16, 8), .MaximumSize = New Size(400, 0)}
    Private ReadOnly btnInclude As New Button() With {.Text = "Include in this purchase", .Tag = "primary", .AutoSize = True}
    Private ReadOnly btnSkip As New Button() With {.Text = "Not now", .AutoSize = True}

    ''' What was chosen to pay now — 0 unless "Include in this purchase" was pressed.
    Public Property Amount As Decimal

    ''' False (the default) means the balance is left untouched by this order.
    Public Property WillPay As Boolean

    Public Sub New(supplierName As String, balance As Decimal, orderCount As Integer, oldestDate As Date?)
        Text = "Previous balance — " & supplierName
        Width = 460
        Height = 320
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        Dim since = If(oldestDate.HasValue, $" — the oldest from {oldestDate.Value:dd MMM yyyy}", "")
        Dim orderWord = If(orderCount = 1, "order", "orders")
        lblInfo.Text =
            $"We already owe {supplierName} {AppInfo.Money(balance)}, from {orderCount} earlier unpaid {orderWord}{since}." &
            vbCrLf & vbCrLf &
            "Pay some or all of it now, on top of this purchase — or leave it for another time; " &
            "either way it stays owed until it's paid."

        numAmount.Maximum = balance
        numAmount.Value = balance

        Dim table = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(table, "Amount to pay now", numAmount)

        Dim buttons As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(12)}
        buttons.Controls.Add(btnSkip)
        buttons.Controls.Add(btnInclude)

        Controls.Add(table)
        Controls.Add(buttons)
        Controls.Add(lblInfo)

        AddHandler btnInclude.Click, Sub(s, e)
                                          Amount = numAmount.Value
                                          WillPay = Amount > 0
                                          DialogResult = If(WillPay, DialogResult.OK, DialogResult.Cancel)
                                          Close()
                                      End Sub
        AddHandler btnSkip.Click, Sub(s, e)
                                       Amount = 0D
                                       WillPay = False
                                       DialogResult = DialogResult.Cancel
                                       Close()
                                   End Sub

        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

End Class
