Imports System.Windows.Forms
Imports System.Data

''' Create a waybill for a sale. Shows the sale and the exact goods going out,
''' then takes driver name / phone, vehicle plate number and the destination
''' address (pre-filled from the customer).
Public Class frmNewWaybill
    Inherits Form

    Private ReadOnly _invoiceId As Integer
    Private ReadOnly _userId As Integer
    Private txtDriver As New TextBox()
    Private txtDriverPhone As New TextBox()
    Private txtPlate As New TextBox()
    Private txtDestination As New TextBox() With {.Multiline = True, .Height = 54}
    Private txtNotes As New TextBox()

    Public ReadOnly Property NewWaybillId As Integer

    Public Sub New(invoiceId As Integer, userId As Integer)
        _invoiceId = invoiceId
        _userId = userId
        Text = "Create waybill"
        Width = 680
        Height = 720
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        Dim r = DataAccess.GetTable(
            "SELECT i.InvoiceNumber, i.InvoiceDate, c.Name, ISNULL(c.Phone,'') AS Phone, ISNULL(c.[Address],'') AS Addr, ISNULL(c.Location,'') AS Loc, " &
            "ISNULL(w.Name,'') AS Warehouse, " &
            "(SELECT COUNT(*) FROM Waybills wb WHERE wb.InvoiceID=i.InvoiceID) AS Existing " &
            "FROM Invoices i JOIN Customers c ON c.CustomerID=i.CustomerID LEFT JOIN Warehouses w ON w.WarehouseID=i.WarehouseID WHERE i.InvoiceID=@id",
            New Dictionary(Of String, Object) From {{"@id", invoiceId}}).Rows(0)
        txtDestination.Text = String.Join(", ", {Convert.ToString(r("Addr")), Convert.ToString(r("Loc"))}.Where(Function(x) x <> ""))

        Dim items = ucWaybill.InvoiceItems(invoiceId)
        Dim totalQty = items.AsEnumerable().Sum(Function(x) Convert.ToInt32(x("Qty")))

        Dim head As New Label() With {.Dock = DockStyle.Top, .AutoSize = True, .Tag = "heading",
            .Padding = New Padding(16, 10, 16, 2), .Text = $"Sale {r("InvoiceNumber")} — {r("Name")}"}
        Dim info As New Label() With {.Dock = DockStyle.Top, .AutoSize = True, .Tag = "keepfont", .Padding = New Padding(16, 2, 16, 6),
            .Text = $"Sold {Convert.ToDateTime(r("InvoiceDate")):dd MMM yyyy}  ·  from {If(Convert.ToString(r("Warehouse")) = "", "—", Convert.ToString(r("Warehouse")))}" &
                    $"{If(Convert.ToString(r("Phone")) = "", "", "  ·  customer phone " & Convert.ToString(r("Phone")))}" & vbCrLf &
                    $"Goods on this waybill: {items.Rows.Count} product line(s), {totalQty:#,0} unit(s)"}
        Dim warn As New Label() With {.Dock = DockStyle.Top, .AutoSize = True, .Tag = "keepfont", .Padding = New Padding(16, 0, 16, 6),
            .ForeColor = Drawing.Color.DarkOrange, .Visible = Convert.ToInt32(r("Existing")) > 0,
            .Text = $"Note: this sale already has {r("Existing")} waybill(s)."}

        Dim grid = UiHelpers.NewGrid()
        grid.Dock = DockStyle.Top
        grid.Height = 200
        grid.DataSource = items
        Dim gridHost As New Panel() With {.Dock = DockStyle.Top, .Height = 210, .Padding = New Padding(16, 0, 16, 6)}
        gridHost.Controls.Add(grid)

        Dim t = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(t, "Driver name", txtDriver)
        UiHelpers.AddLabeled(t, "Driver phone", txtDriverPhone)
        UiHelpers.AddLabeled(t, "Vehicle plate no.", txtPlate)
        UiHelpers.AddLabeled(t, "Destination", txtDestination)
        UiHelpers.AddLabeled(t, "Notes", txtNotes)

        ' Dock=Top controls stack bottom-up in add order: last added sits on top.
        Controls.Add(t)
        Controls.Add(gridHost)
        Controls.Add(warn)
        Controls.Add(info)
        Controls.Add(head)
        UiHelpers.AddOkCancelRow(Me, "Create waybill", AddressOf Save_Click)
        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        Dim now = DateTime.Now
        Dim number = $"ChewyStock-{now:ddMMyyyy}-{now:HHmmss}"
        _NewWaybillId = DataAccess.ExecuteScalarInsert(
            "INSERT INTO Waybills (WaybillNumber, InvoiceID, DriverName, DriverPhone, VehiclePlate, DestinationAddress, Notes, CreatedByUserID) " &
            "VALUES (@n, @i, @d, @dp, @pl, @dest, @note, @u)",
            New Dictionary(Of String, Object) From {
                {"@n", number}, {"@i", _invoiceId},
                {"@d", NullIf(txtDriver)}, {"@dp", NullIf(txtDriverPhone)}, {"@pl", NullIf(txtPlate)},
                {"@dest", NullIf(txtDestination)}, {"@note", NullIf(txtNotes)}, {"@u", _userId}})
        AppUI.Toast("Waybill " & number & " created.", AppUI.ToastKind.Success)
        Anim.SuccessTick(If(Owner, Me), "Waybill created")
        DialogResult = DialogResult.OK
        Close()
    End Sub

    Private Shared Function NullIf(tb As TextBox) As Object
        Return If(String.IsNullOrWhiteSpace(tb.Text), CObj(DBNull.Value), tb.Text.Trim())
    End Function

End Class
