Imports System.Windows.Forms

''' Tiny reusable "enter an amount (+ optional note)" dialog.
Public Class frmMoneyPrompt
    Inherits Form

    Private ReadOnly numAmount As New NumericUpDown() With {.Maximum = 1000000000, .DecimalPlaces = 2}
    Private ReadOnly txtNote As New TextBox()

    Public ReadOnly Property Amount As Decimal
        Get
            Return numAmount.Value
        End Get
    End Property
    Public ReadOnly Property NoteText As Object
        Get
            Return If(String.IsNullOrWhiteSpace(txtNote.Text), CObj(DBNull.Value), txtNote.Text.Trim())
        End Get
    End Property

    Public Sub New(title As String, amountLabel As String, Optional noteLabel As String = "Note")
        Text = title
        Width = 360
        Height = 220
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        Dim t = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(t, amountLabel, numAmount)
        UiHelpers.AddLabeled(t, noteLabel, txtNote)
        Controls.Add(t)
        UiHelpers.AddOkCancelRow(Me, "OK", Sub(s, e)
                                              DialogResult = DialogResult.OK
                                              Close()
                                          End Sub)
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

End Class
