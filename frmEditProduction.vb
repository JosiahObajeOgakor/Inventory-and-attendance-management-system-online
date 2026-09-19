Imports System.Windows.Forms
Imports System.Data

''' Admin-only correction of a production entry typed wrong (e.g. 61 bags
''' instead of 41). Changing the quantity moves the warehouse stock by the
''' difference; the product and warehouse stay as recorded.
Public Class frmEditProduction
    Inherits Form

    Private ReadOnly _movementId As Integer
    Private ReadOnly _userId As Integer
    Private numQty As New NumericUpDown() With {.Minimum = 1, .Maximum = 1000000}
    Private dtpDate As New DateTimePicker() With {.Format = DateTimePickerFormat.Custom, .CustomFormat = "dd MMM yyyy"}

    Public Sub New(movementId As Integer, userId As Integer)
        _movementId = movementId
        _userId = userId
        Text = "Edit production entry"
        Width = 480
        Height = 340
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        Dim r = DataAccess.GetTable(
            "SELECT p.Name AS Product, p.Unit, w.Name AS Warehouse, sm.Quantity, sm.MovementDate " &
            "FROM StockMovements sm JOIN Products p ON p.ProductID = sm.ProductID " &
            "JOIN Warehouses w ON w.WarehouseID = sm.WarehouseID WHERE sm.MovementID = @m",
            New Dictionary(Of String, Object) From {{"@m", movementId}}).Rows(0)

        dtpDate.MaxDate = Date.Today
        numQty.Value = Math.Max(1, Convert.ToInt32(r("Quantity")))
        dtpDate.Value = Convert.ToDateTime(r("MovementDate")).Date

        Dim t = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(t, "Product", New Label() With {.Text = Convert.ToString(r("Product")), .AutoSize = True})
        UiHelpers.AddLabeled(t, "Warehouse", New Label() With {.Text = Convert.ToString(r("Warehouse")), .AutoSize = True})
        UiHelpers.AddLabeled(t, "Quantity produced", numQty)
        UiHelpers.AddLabeled(t, "Produced on", dtpDate)

        Dim head As New Label() With {.Dock = DockStyle.Top, .AutoSize = True, .Tag = "keepfont", .MaximumSize = New Drawing.Size(440, 0),
            .Padding = New Padding(16, 10, 16, 8),
            .Text = $"Recorded as {Convert.ToInt32(r("Quantity")):#,0} {Convert.ToString(r("Unit")).ToLowerInvariant()}(s). The warehouse stock is adjusted by the difference."}

        Controls.Add(t)
        Controls.Add(head)
        UiHelpers.AddOkCancelRow(Me, "Save correction", AddressOf Save_Click)
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        Dim err = Stock.CorrectProduction(_movementId, CInt(numQty.Value), dtpDate.Value.Date, _userId)
        If err <> "" Then
            AppUI.Toast("Could not save: " & err, AppUI.ToastKind.Error)
            Return
        End If
        AppUI.Toast("Production entry corrected.", AppUI.ToastKind.Success)
        Anim.SuccessTick(If(Owner, Me), "Entry corrected")
        DialogResult = DialogResult.OK
        Close()
    End Sub

End Class
