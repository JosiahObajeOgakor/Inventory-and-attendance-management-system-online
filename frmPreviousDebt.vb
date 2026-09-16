Imports System.Windows.Forms
Imports System.Drawing

''' Shown the moment a customer with an outstanding balance is picked for a new
''' sale — so an old debt is never something the seller has to remember to ask
''' about on their own. Offers to fold some or all of it into what's collected
''' today; skipping it leaves it exactly where it was, on the customer's account.
Public Class frmPreviousDebt
    Inherits Form

    Private ReadOnly numAmount As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private ReadOnly lblInfo As New Label() With {
        .Dock = DockStyle.Top, .AutoSize = True, .Tag = "keepfont",
        .Padding = New Padding(16, 16, 16, 8), .MaximumSize = New Size(400, 0)}
    Private ReadOnly btnInclude As New Button() With {.Text = "Include in this sale", .Tag = "primary", .AutoSize = True}
    Private ReadOnly btnSkip As New Button() With {.Text = "Not now", .AutoSize = True}

    ''' What was chosen to collect now — 0 unless "Include in this sale" was pressed.
    Public Property Amount As Decimal

    ''' False (the default) means the balance is left untouched by this sale.
    Public Property WillCollect As Boolean

    Public Sub New(customerName As String, balance As Decimal, invoiceCount As Integer, oldestDate As Date?)
        Text = "Previous balance — " & customerName
        Width = 460
        Height = 320
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        Dim since = If(oldestDate.HasValue, $" — the oldest from {oldestDate.Value:dd MMM yyyy}", "")
        Dim invoiceWord = If(invoiceCount = 1, "invoice", "invoices")
        lblInfo.Text =
            $"{customerName} already owes {AppInfo.Money(balance)}, from {invoiceCount} earlier unpaid {invoiceWord}{since}." &
            vbCrLf & vbCrLf &
            "Collect some or all of it now, on top of today's purchase — or leave it for another time; " &
            "either way it stays on their account until it's paid."

        numAmount.Maximum = balance
        numAmount.Value = balance

        Dim table = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(table, "Amount to collect now", numAmount)

        Dim buttons As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(12)}
        buttons.Controls.Add(btnSkip)
        buttons.Controls.Add(btnInclude)

        Controls.Add(table)
        Controls.Add(buttons)
        Controls.Add(lblInfo)

        AddHandler btnInclude.Click, Sub(s, e)
                                          Amount = numAmount.Value
                                          WillCollect = Amount > 0
                                          DialogResult = If(WillCollect, DialogResult.OK, DialogResult.Cancel)
                                          Close()
                                      End Sub
        AddHandler btnSkip.Click, Sub(s, e)
                                       Amount = 0D
                                       WillCollect = False
                                       DialogResult = DialogResult.Cancel
                                       Close()
                                   End Sub

        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

End Class
