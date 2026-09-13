Imports System.Windows.Forms

''' Add-expense dialog.
Public Class frmAddExpense
    Inherits Form

    Private cboCategory As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private dtpDate As New DateTimePicker() With {.Format = DateTimePickerFormat.Short}
    Private numAmount As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private txtNote As New TextBox()

    Public ReadOnly Property Category As String
        Get
            Return cboCategory.Text
        End Get
    End Property
    Public ReadOnly Property ExpenseDate As Date
        Get
            Return dtpDate.Value.Date
        End Get
    End Property
    Public ReadOnly Property Amount As Decimal
        Get
            Return numAmount.Value
        End Get
    End Property
    Public ReadOnly Property Note As String
        Get
            Return If(String.IsNullOrWhiteSpace(txtNote.Text), "—", txtNote.Text.Trim())
        End Get
    End Property

    Public Sub New()
        Text = "Add expense"
        Width = 380
        Height = 340
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False

        cboCategory.Items.AddRange({"Rent", "Salaries", "Utilities", "Logistics", "Maintenance", "Other"})
        cboCategory.SelectedIndex = 0

        UiHelpers.AddOkCancelRow(Me, "Save expense", AddressOf btnSave_Click)
        Dim table = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(table, "Category", cboCategory)
        UiHelpers.AddLabeled(table, "Date", dtpDate)
        UiHelpers.AddLabeled(table, "Amount", numAmount)
        UiHelpers.AddLabeled(table, "Note", txtNote)
        Controls.Add(table)
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
