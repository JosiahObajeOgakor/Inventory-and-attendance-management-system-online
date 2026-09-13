Imports System.Windows.Forms

''' Amount-received dialog for a specific customer, opened from ucCustomers.
Public Class frmRecordPayment
    Inherits Form

    Private lblInfo As New Label() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 16, 16, 0)}
    Private numAmount As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}

    Public ReadOnly Property Amount As Decimal
        Get
            Return numAmount.Value
        End Get
    End Property

    Public Sub New(customerName As String, currentBalance As Decimal)
        Text = "Record payment"
        Width = 360
        Height = 220
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False

        lblInfo.Text = customerName & " — balance ₦" & currentBalance.ToString("N0")
        numAmount.Maximum = currentBalance

        UiHelpers.AddOkCancelRow(Me, "Save payment", AddressOf btnSave_Click)
        Dim table = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(table, "Amount received", numAmount)
        Controls.Add(table)
        Controls.Add(lblInfo)
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub btnSave_Click(sender As Object, e As EventArgs)
        If Amount <= 0 Then
            MessageBox.Show("Enter an amount greater than zero.", "Missing amount", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        Me.DialogResult = DialogResult.OK
        Me.Close()
    End Sub

End Class
