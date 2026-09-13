Imports System.Windows.Forms

''' Edit an existing product's cost + the three selling prices (distributor,
''' wholesaler, retail) and its reorder level. SellingPrice (legacy column) is
''' kept in sync with the retail tier.
Public Class frmProductPrices
    Inherits Form

    Private ReadOnly _productId As Integer
    Private ReadOnly numCost As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private ReadOnly numDist As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private ReadOnly numWhole As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private ReadOnly numRetail As New NumericUpDown() With {.Maximum = 100000000, .DecimalPlaces = 2}
    Private ReadOnly numReorder As New NumericUpDown() With {.Maximum = 100000}
    Private ReadOnly lblName As New Label() With {.AutoSize = True, .Tag = "heading"}

    Public Sub New(productId As Integer)
        _productId = productId
        Text = "Edit product prices"
        Width = 380
        Height = 340
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        Dim r = DataAccess.GetTable(
            "SELECT Name, CostPrice, PriceDistributor, PriceWholesaler, PriceRetail, ReorderLevel FROM Products WHERE ProductID = @id",
            New Dictionary(Of String, Object) From {{"@id", productId}}).Rows(0)
        lblName.Text = Convert.ToString(r("Name"))
        numCost.Value = Convert.ToDecimal(r("CostPrice"))
        numDist.Value = Convert.ToDecimal(r("PriceDistributor"))
        numWhole.Value = Convert.ToDecimal(r("PriceWholesaler"))
        numRetail.Value = Convert.ToDecimal(r("PriceRetail"))
        numReorder.Value = Convert.ToInt32(r("ReorderLevel"))

        Dim t = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(t, "Cost price", numCost)
        UiHelpers.AddLabeled(t, "Distributor price", numDist)
        UiHelpers.AddLabeled(t, "Wholesaler price", numWhole)
        UiHelpers.AddLabeled(t, "Retail price", numRetail)
        UiHelpers.AddLabeled(t, "Reorder level", numReorder)

        Dim namePanel As New Panel() With {.Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(16, 8, 0, 0)}
        namePanel.Controls.Add(lblName)

        Controls.Add(t)
        Controls.Add(namePanel)
        UiHelpers.AddOkCancelRow(Me, "Save prices", AddressOf Save_Click)
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        DataAccess.Execute(
            "UPDATE Products SET CostPrice=@c, PriceDistributor=@d, PriceWholesaler=@w, PriceRetail=@r, SellingPrice=@r, ReorderLevel=@ro WHERE ProductID=@id",
            New Dictionary(Of String, Object) From {
                {"@c", numCost.Value}, {"@d", numDist.Value}, {"@w", numWhole.Value},
                {"@r", numRetail.Value}, {"@ro", CInt(numReorder.Value)}, {"@id", _productId}})
        DialogResult = DialogResult.OK
        Close()
    End Sub

End Class
