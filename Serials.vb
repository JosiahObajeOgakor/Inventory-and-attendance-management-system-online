Imports System.Data
Imports System.Data.SqlClient

''' Serial number tracking for goods where an individual unit matters — scales,
''' feeders, freezers — as opposed to loose feed, which is only ever counted.
'''
''' A product opts in with Products.TracksSerial. Once it has, every unit that
''' comes in gets a row here, and a sale consumes named units rather than just
''' decrementing a number, so a warranty claim can be walked back to its invoice.
Public Module Serials

    Public Class SerialUnit
        Public Property SerialID As Integer
        Public Property ProductID As Integer
        Public Property SerialNumber As String
        Public Property Status As String
        Public Property WarehouseID As Integer?
        Public Property InvoiceNumber As String
        Public Property ReceivedAt As Date
        Public Property SoldAt As Date?
    End Class

    Public Function IsTracked(productId As Integer) As Boolean
        Dim t = DataAccess.GetTable("SELECT TracksSerial FROM Products WHERE ProductID = @p",
                                    New Dictionary(Of String, Object) From {{"@p", productId}})
        Return t.Rows.Count > 0 AndAlso Convert.ToBoolean(t.Rows(0)(0))
    End Function

    Public Function AvailableCount(productId As Integer, Optional warehouseId As Integer? = Nothing) As Integer
        Dim sql = "SELECT COUNT(*) FROM ProductSerials WHERE ProductID = @p AND Status = 'In Stock'"
        Dim p As New Dictionary(Of String, Object) From {{"@p", productId}}
        If warehouseId.HasValue Then
            sql &= " AND WarehouseID = @w"
            p("@w") = warehouseId.Value
        End If
        Return Convert.ToInt32(DataAccess.GetTable(sql, p).Rows(0)(0))
    End Function

    ''' Serials on the shelf for this product, oldest received first — the order a
    ''' picker should hand them out so nothing ages in a corner.
    Public Function Available(productId As Integer, Optional warehouseId As Integer? = Nothing) As DataTable
        Dim sql =
            "SELECT SerialID, SerialNumber, WarehouseID, ReceivedAt " &
            "FROM ProductSerials WHERE ProductID = @p AND Status = 'In Stock'"
        Dim p As New Dictionary(Of String, Object) From {{"@p", productId}}
        If warehouseId.HasValue Then
            sql &= " AND WarehouseID = @w"
            p("@w") = warehouseId.Value
        End If
        Return DataAccess.GetTable(sql & " ORDER BY ReceivedAt, SerialID", p)
    End Function

    ''' The full life of one unit, for a warranty desk: who it went to and when.
    Public Function History(productId As Integer, serialNumber As String) As DataTable
        Return DataAccess.GetTable(
            "SELECT s.SerialNumber, s.Status, s.ReceivedAt, s.SoldAt, i.InvoiceNumber, c.Name AS Customer, s.Notes " &
            "FROM ProductSerials s " &
            "LEFT JOIN Invoices i ON i.InvoiceID = s.InvoiceID " &
            "LEFT JOIN Customers c ON c.CustomerID = i.CustomerID " &
            "WHERE s.ProductID = @p AND s.SerialNumber = @s",
            New Dictionary(Of String, Object) From {{"@p", productId}, {"@s", serialNumber}})
    End Function

    ''' Books newly received units in. Rejects the whole list if any serial is
    ''' already on file for this product — a duplicate serial means either a
    ''' typo or a counterfeit, and neither should be waved through.
    ''' Returns "" on success, or an error message.
    Public Function Receive(productId As Integer, warehouseId As Integer, serialNumbers As IEnumerable(Of String),
                            Optional batchId As Integer? = Nothing, Optional notes As String = Nothing) As String
        Dim wanted = Clean(serialNumbers)
        If wanted.Count = 0 Then Return "Enter at least one serial number."

        Dim dupWithin = wanted.GroupBy(Function(s) s, StringComparer.OrdinalIgnoreCase).Where(Function(g) g.Count() > 1).Select(Function(g) g.Key).ToList()
        If dupWithin.Count > 0 Then Return "The same serial is listed twice: " & String.Join(", ", dupWithin)

        Try
            Return DataAccess.InTransaction(Function(conn, tx)
                                                Dim clash = ExistingSerials(conn, tx, productId, wanted)
                                                If clash.Count > 0 Then Return "Already on file: " & String.Join(", ", clash)
                                                For Each sn In wanted
                                                    DataAccess.Exec(conn, tx,
                                                        "INSERT INTO ProductSerials (ProductID, SerialNumber, BatchID, WarehouseID, Status, ReceivedAt, Notes) " &
                                                        "VALUES (@p, @s, @b, @w, 'In Stock', @t, @n)",
                                                        New Dictionary(Of String, Object) From {
                                                            {"@p", productId}, {"@s", sn},
                                                            {"@b", If(batchId.HasValue, CObj(batchId.Value), DBNull.Value)},
                                                            {"@w", warehouseId}, {"@t", DateTime.Now},
                                                            {"@n", If(String.IsNullOrWhiteSpace(notes), CObj(DBNull.Value), notes)}})
                                                Next
                                                Return ""
                                            End Function)
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

    ''' Marks the given serials sold against an invoice, inside the caller's
    ''' transaction so it commits or rolls back with the rest of the sale.
    ''' Throws when a serial isn't on the shelf — the sale must not go through
    ''' claiming a unit the shop cannot hand over.
    Public Sub ConsumeForSale(conn As SqlConnection, tx As SqlTransaction, productId As Integer,
                              serialNumbers As IEnumerable(Of String), invoiceId As Integer, soldAt As Date)
        Dim wanted = Clean(serialNumbers)
        If wanted.Count = 0 Then Return

        For Each sn In wanted
            Dim moved = DataAccess.Exec(conn, tx,
                "UPDATE ProductSerials SET Status = 'Sold', SoldAt = @t, InvoiceID = @i " &
                "WHERE ProductID = @p AND SerialNumber = @s AND Status = 'In Stock'",
                New Dictionary(Of String, Object) From {
                    {"@t", soldAt}, {"@i", invoiceId}, {"@p", productId}, {"@s", sn}})
            If moved <> 1 Then
                Throw New InvalidOperationException(
                    $"Serial {sn} is not in stock — it may already be sold, returned or mistyped.")
            End If
        Next
    End Sub

    ''' A customer brings a unit back: it returns to the shelf and is sellable again.
    Public Function TakeBack(productId As Integer, serialNumber As String, warehouseId As Integer, reason As String) As String
        Try
            Dim moved = DataAccess.Execute(
                "UPDATE ProductSerials SET Status = 'Returned', WarehouseID = @w, Notes = @n " &
                "WHERE ProductID = @p AND SerialNumber = @s AND Status = 'Sold'",
                New Dictionary(Of String, Object) From {
                    {"@w", warehouseId}, {"@n", If(String.IsNullOrWhiteSpace(reason), CObj(DBNull.Value), reason)},
                    {"@p", productId}, {"@s", serialNumber}})
            If moved <> 1 Then Return "That serial isn't recorded as sold, so there is nothing to take back."
            Return ""
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

    Public Function WriteOff(productId As Integer, serialNumber As String, reason As String) As String
        Try
            Dim moved = DataAccess.Execute(
                "UPDATE ProductSerials SET Status = 'Written Off', Notes = @n " &
                "WHERE ProductID = @p AND SerialNumber = @s AND Status IN ('In Stock', 'Returned')",
                New Dictionary(Of String, Object) From {
                    {"@n", If(String.IsNullOrWhiteSpace(reason), CObj(DBNull.Value), reason)},
                    {"@p", productId}, {"@s", serialNumber}})
            If moved <> 1 Then Return "Only a unit still on the shelf can be written off."
            Return ""
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

    ''' Where the counted stock and the serial register disagree. On a serialised
    ''' product these two must match; a gap means units were moved without being
    ''' logged, which is exactly what a stock-take is looking for.
    Public Function Discrepancies() As DataTable
        Return DataAccess.GetTable(
            "SELECT p.ProductID, p.SKU, p.Name, " &
            "       ISNULL(b.Counted, 0) AS CountedStock, " &
            "       ISNULL(s.Registered, 0) AS SerialsOnShelf, " &
            "       ISNULL(b.Counted, 0) - ISNULL(s.Registered, 0) AS Difference " &
            "FROM Products p " &
            "LEFT JOIN (SELECT ProductID, SUM(QuantityOnHand) AS Counted FROM StockBatches GROUP BY ProductID) b ON b.ProductID = p.ProductID " &
            "LEFT JOIN (SELECT ProductID, COUNT(*) AS Registered FROM ProductSerials WHERE Status = 'In Stock' GROUP BY ProductID) s ON s.ProductID = p.ProductID " &
            "WHERE p.TracksSerial = 1 AND ISNULL(b.Counted, 0) <> ISNULL(s.Registered, 0) " &
            "ORDER BY ABS(ISNULL(b.Counted, 0) - ISNULL(s.Registered, 0)) DESC")
    End Function

    Private Function ExistingSerials(conn As SqlConnection, tx As SqlTransaction, productId As Integer,
                                     wanted As List(Of String)) As List(Of String)
        Dim found As New List(Of String)
        For Each sn In wanted
            Dim hit = DataAccess.ScalarIn(conn, tx,
                "SELECT COUNT(*) FROM ProductSerials WHERE ProductID = @p AND SerialNumber = @s",
                New Dictionary(Of String, Object) From {{"@p", productId}, {"@s", sn}})
            If Convert.ToInt32(hit) > 0 Then found.Add(sn)
        Next
        Return found
    End Function

    ''' Accepts a pasted block, one serial per line or comma separated, which is
    ''' how they arrive off a supplier's packing list.
    Public Function Split(pasted As String) As List(Of String)
        If String.IsNullOrWhiteSpace(pasted) Then Return New List(Of String)
        Return Clean(pasted.Split({vbCr, vbLf, ",", ";", vbTab}, StringSplitOptions.RemoveEmptyEntries))
    End Function

    Private Function Clean(values As IEnumerable(Of String)) As List(Of String)
        If values Is Nothing Then Return New List(Of String)
        Return values.Select(Function(s) If(s, "").Trim()).Where(Function(s) s.Length > 0).ToList()
    End Function

End Module
