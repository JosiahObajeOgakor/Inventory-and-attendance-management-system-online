Imports System.Data

''' Stock movements that aren't part of a sale or purchase — the production
''' entries staff record after each run (and admin corrections to them), and
''' moves between warehouses.
Public Module Stock

    ''' Adds newly produced goods to a warehouse and logs the run in the stock
    ''' history under the production date. Tops up the batch if that batch number
    ''' already exists there, otherwise starts a new one.
    ''' Returns "" on success, or an error message.
    Public Function RecordProduction(productId As Integer, warehouseId As Integer, quantity As Integer,
                                     producedOn As Date, batchNumber As String, expiry As Date?,
                                     userId As Integer) As String
        If quantity <= 0 Then Return "Enter how many were produced."
        Dim batch = If(String.IsNullOrWhiteSpace(batchNumber), "PROD-" & producedOn.ToString("yyMMdd"), batchNumber.Trim())

        Dim p As New Dictionary(Of String, Object) From {
            {"@p", productId}, {"@w", warehouseId}, {"@q", quantity}, {"@b", batch},
            {"@e", If(expiry.HasValue, CObj(expiry.Value.Date), DBNull.Value)},
            {"@d", producedOn.Date}, {"@u", userId}}

        Try
            DataAccess.Execute(
                "BEGIN TRANSACTION; " &
                "UPDATE StockBatches SET QuantityOnHand = QuantityOnHand + @q, " &
                "  ExpiryDate = CASE WHEN @e IS NULL THEN ExpiryDate ELSE @e END " &
                "  WHERE ProductID = @p AND WarehouseID = @w AND BatchNumber = @b; " &
                "IF @@ROWCOUNT = 0 " &
                "  INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, ExpiryDate, QuantityOnHand) " &
                "  VALUES (@p, @w, @b, @e, @q); " &
                "INSERT INTO StockMovements (ProductID, WarehouseID, MovementType, Quantity, ReferenceType, ReferenceID, MovementDate, UserID) " &
                "  SELECT @p, @w, 'IN', @q, 'Production', BatchID, @d, @u FROM StockBatches " &
                "  WHERE ProductID = @p AND WarehouseID = @w AND BatchNumber = @b; " &
                "COMMIT;", p)
            Return ""
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

    ''' Corrects a production entry that was typed wrong (say 61 bags instead of
    ''' 41): the history row takes the new quantity and date, and the warehouse
    ''' stock moves by the difference. A new quantity of 0 deletes the entry.
    ''' Stock taken off comes from the batch the run went into first, then the
    ''' newest other batches in that warehouse. Refused when that much is no
    ''' longer there (already sold or moved), so stock can never go negative.
    ''' Returns "" on success, or an error message.
    Public Function CorrectProduction(movementId As Integer, newQuantity As Integer, producedOn As Date, userId As Integer) As String
        If newQuantity < 0 Then Return "Quantity cannot be negative."
        Try
            Return DataAccess.InTransaction(Of String)(
                Function(conn, tx)
                    Dim m = DataAccess.TableIn(conn, tx,
                        "SELECT sm.ProductID, sm.WarehouseID, sm.Quantity, sm.ReferenceID, sm.MovementDate, w.Name AS Warehouse " &
                        "FROM StockMovements sm WITH (UPDLOCK) JOIN Warehouses w ON w.WarehouseID = sm.WarehouseID " &
                        "WHERE sm.MovementID = @m AND sm.ReferenceType = 'Production'",
                        New Dictionary(Of String, Object) From {{"@m", movementId}})
                    If m.Rows.Count = 0 Then Return "That production entry no longer exists."
                    Dim r = m.Rows(0)
                    Dim productId = Convert.ToInt32(r("ProductID")), warehouseId = Convert.ToInt32(r("WarehouseID"))
                    Dim oldQty = Convert.ToInt32(r("Quantity"))
                    Dim batchId = If(IsDBNull(r("ReferenceID")), 0, Convert.ToInt32(r("ReferenceID")))
                    Dim dayBatch = "PROD-" & Convert.ToDateTime(r("MovementDate")).ToString("yyMMdd")
                    Dim delta = newQuantity - oldQty

                    If delta > 0 Then
                        AddToBatch(conn, tx, productId, warehouseId, batchId, dayBatch, delta)
                    ElseIf delta < 0 Then
                        Dim err = TakeFromWarehouse(conn, tx, productId, warehouseId, batchId, dayBatch, -delta, Convert.ToString(r("Warehouse")))
                        If err <> "" Then
                            Dim lowest = oldQty - OnHandIn(conn, tx, productId, warehouseId)
                            Return $"{err} This entry can't go below {lowest:#,0}."
                        End If
                    End If

                    If newQuantity = 0 Then
                        DataAccess.Exec(conn, tx, "DELETE FROM StockMovements WHERE MovementID = @m",
                                        New Dictionary(Of String, Object) From {{"@m", movementId}})
                    Else
                        DataAccess.Exec(conn, tx,
                            "UPDATE StockMovements SET Quantity = @q, MovementDate = @d WHERE MovementID = @m",
                            New Dictionary(Of String, Object) From {{"@q", newQuantity}, {"@d", producedOn.Date}, {"@m", movementId}})
                    End If
                    Return ""
                End Function)
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

    Private Function OnHandIn(conn As SqlClient.SqlConnection, tx As SqlClient.SqlTransaction,
                              productId As Integer, warehouseId As Integer) As Integer
        Return Convert.ToInt32(DataAccess.ScalarIn(conn, tx,
            "SELECT ISNULL(SUM(QuantityOnHand), 0) FROM StockBatches WHERE ProductID = @p AND WarehouseID = @w",
            New Dictionary(Of String, Object) From {{"@p", productId}, {"@w", warehouseId}}))
    End Function

    ''' Puts units back into the batch a production run went into, or that day's
    ''' PROD- batch when the entry predates batch tracking.
    Private Sub AddToBatch(conn As SqlClient.SqlConnection, tx As SqlClient.SqlTransaction,
                           productId As Integer, warehouseId As Integer, batchId As Integer, dayBatch As String, qty As Integer)
        Dim p As New Dictionary(Of String, Object) From {
            {"@id", batchId}, {"@p", productId}, {"@w", warehouseId}, {"@b", dayBatch}, {"@q", qty}}
        If DataAccess.Exec(conn, tx,
            "UPDATE StockBatches SET QuantityOnHand = QuantityOnHand + @q WHERE BatchID = @id AND ProductID = @p AND WarehouseID = @w", p) > 0 Then Return
        If DataAccess.Exec(conn, tx,
            "UPDATE StockBatches SET QuantityOnHand = QuantityOnHand + @q WHERE ProductID = @p AND WarehouseID = @w AND BatchNumber = @b", p) > 0 Then Return
        DataAccess.Exec(conn, tx,
            "INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p, @w, @b, @q)", p)
    End Sub

    ''' Removes units from one warehouse — the given batch first, then that
    ''' day's PROD- batch, then the newest others. Returns "" or why it can't.
    Private Function TakeFromWarehouse(conn As SqlClient.SqlConnection, tx As SqlClient.SqlTransaction,
                                       productId As Integer, warehouseId As Integer, batchId As Integer, dayBatch As String,
                                       qty As Integer, warehouseName As String) As String
        Dim batches = DataAccess.TableIn(conn, tx,
            "SELECT BatchID, QuantityOnHand FROM StockBatches WITH (UPDLOCK, HOLDLOCK) " &
            "WHERE ProductID = @p AND WarehouseID = @w AND QuantityOnHand > 0 " &
            "ORDER BY CASE WHEN BatchID = @id THEN 0 WHEN BatchNumber = @b THEN 1 ELSE 2 END, BatchID DESC",
            New Dictionary(Of String, Object) From {{"@p", productId}, {"@w", warehouseId}, {"@id", batchId}, {"@b", dayBatch}})
        Dim have = batches.AsEnumerable().Sum(Function(b) Convert.ToInt32(b("QuantityOnHand")))
        If have < qty Then
            Return $"Only {have:#,0} left in {warehouseName} — the rest has already been sold or moved."
        End If
        Dim remaining = qty
        For Each b As DataRow In batches.Rows
            If remaining = 0 Then Exit For
            Dim take = Math.Min(remaining, Convert.ToInt32(b("QuantityOnHand")))
            DataAccess.Exec(conn, tx, "UPDATE StockBatches SET QuantityOnHand = QuantityOnHand - @t WHERE BatchID = @id",
                            New Dictionary(Of String, Object) From {{"@t", take}, {"@id", Convert.ToInt32(b("BatchID"))}})
            remaining -= take
        Next
        Return ""
    End Function

    ''' Moves stock from one warehouse to another — e.g. topping up Shore from
    ''' Lawal when Shore runs out, or back the other way when Lawal is short for
    ''' a big order. The company total doesn't change. Units keep their batch
    ''' number and expiry, earliest-expiring moved first. Logged as an OUT from
    ''' one and an IN to the other (ReferenceType 'Transfer', ReferenceID = the
    ''' other warehouse). Returns "" on success, or an error message.
    Public Function TransferStock(productId As Integer, fromWarehouseId As Integer, toWarehouseId As Integer,
                                  quantity As Integer, userId As Integer) As String
        If quantity <= 0 Then Return "Enter how many to move."
        If fromWarehouseId = toWarehouseId Then Return "Pick two different warehouses."
        Try
            Return DataAccess.InTransaction(Of String)(
                Function(conn, tx)
                    Dim batches = DataAccess.TableIn(conn, tx,
                        "SELECT BatchID, BatchNumber, ExpiryDate, QuantityOnHand FROM StockBatches WITH (UPDLOCK, HOLDLOCK) " &
                        "WHERE ProductID = @p AND WarehouseID = @w AND QuantityOnHand > 0 " &
                        "ORDER BY CASE WHEN ExpiryDate IS NULL THEN 1 ELSE 0 END, ExpiryDate, BatchID",
                        New Dictionary(Of String, Object) From {{"@p", productId}, {"@w", fromWarehouseId}})
                    Dim have = batches.AsEnumerable().Sum(Function(b) Convert.ToInt32(b("QuantityOnHand")))
                    If have < quantity Then
                        Dim fromName = Convert.ToString(DataAccess.ScalarIn(conn, tx, "SELECT Name FROM Warehouses WHERE WarehouseID = @w",
                                                        New Dictionary(Of String, Object) From {{"@w", fromWarehouseId}}))
                        Return $"Only {have:#,0} in {fromName} — can't move {quantity:#,0}."
                    End If

                    Dim remaining = quantity
                    For Each b As DataRow In batches.Rows
                        If remaining = 0 Then Exit For
                        Dim take = Math.Min(remaining, Convert.ToInt32(b("QuantityOnHand")))
                        Dim p As New Dictionary(Of String, Object) From {
                            {"@t", take}, {"@id", Convert.ToInt32(b("BatchID"))}, {"@p", productId},
                            {"@to", toWarehouseId}, {"@b", b("BatchNumber")}, {"@e", b("ExpiryDate")}}
                        DataAccess.Exec(conn, tx, "UPDATE StockBatches SET QuantityOnHand = QuantityOnHand - @t WHERE BatchID = @id", p)
                        DataAccess.Exec(conn, tx,
                            "UPDATE StockBatches SET QuantityOnHand = QuantityOnHand + @t WHERE ProductID = @p AND WarehouseID = @to AND BatchNumber = @b; " &
                            "IF @@ROWCOUNT = 0 INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, ExpiryDate, QuantityOnHand) " &
                            "  VALUES (@p, @to, @b, @e, @t);", p)
                        remaining -= take
                    Next

                    Dim mp As New Dictionary(Of String, Object) From {
                        {"@p", productId}, {"@from", fromWarehouseId}, {"@to", toWarehouseId}, {"@q", quantity}, {"@u", userId}}
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO StockMovements (ProductID, WarehouseID, MovementType, Quantity, ReferenceType, ReferenceID, UserID) " &
                        "VALUES (@p, @from, 'OUT', @q, 'Transfer', @to, @u), (@p, @to, 'IN', @q, 'Transfer', @from, @u)", mp)
                    Return ""
                End Function)
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

    ''' Total units of a product held in one warehouse.
    Public Function QuantityInWarehouse(productId As Integer, warehouseId As Integer) As Integer
        Dim t = DataAccess.GetTable(
            "SELECT ISNULL(SUM(QuantityOnHand), 0) FROM StockBatches WHERE ProductID = @p AND WarehouseID = @w",
            New Dictionary(Of String, Object) From {{"@p", productId}, {"@w", warehouseId}})
        Return Convert.ToInt32(t.Rows(0)(0))
    End Function

    ''' What a sale can actually draw on, split by the warehouse it would come
    ''' off first — a sale falls back to other warehouses only when it has to.
    Public Class Availability
        Public Property ProductID As Integer
        Public Property ProductName As String
        Public Property InWarehouse As Integer
        Public Property Elsewhere As Integer
        Public ReadOnly Property Total As Integer
            Get
                Return InWarehouse + Elsewhere
            End Get
        End Property
    End Class

    Public Function AvailabilityFor(productId As Integer, warehouseId As Integer) As Availability
        Dim t = DataAccess.GetTable(
            "SELECT p.Name, " &
            "  ISNULL(SUM(CASE WHEN sb.WarehouseID = @w THEN sb.QuantityOnHand END), 0) AS Here, " &
            "  ISNULL(SUM(CASE WHEN sb.WarehouseID <> @w THEN sb.QuantityOnHand END), 0) AS Other " &
            "FROM Products p LEFT JOIN StockBatches sb ON sb.ProductID = p.ProductID " &
            "WHERE p.ProductID = @p GROUP BY p.Name",
            New Dictionary(Of String, Object) From {{"@p", productId}, {"@w", warehouseId}})
        If t.Rows.Count = 0 Then Return New Availability() With {.ProductID = productId}
        Return New Availability() With {
            .ProductID = productId,
            .ProductName = Convert.ToString(t.Rows(0)("Name")),
            .InWarehouse = Convert.ToInt32(t.Rows(0)("Here")),
            .Elsewhere = Convert.ToInt32(t.Rows(0)("Other"))}
    End Function

    ''' Restocking advice for one product, worked out from what it has actually
    ''' been selling rather than a fixed threshold: average daily demand over the
    ''' trailing window drives the reorder point (lead-time demand + safety
    ''' stock) and the suggested order quantity.
    Public Class RestockAdvice
        Public Property ProductID As Integer
        Public Property ProductName As String
        Public Property OnHand As Integer
        Public Property ReorderLevel As Integer
        Public Property AvgDailyDemand As Decimal
        Public Property DaysOfCover As Decimal?
        Public Property ReorderPoint As Integer
        Public Property SuggestedOrderQty As Integer
        Public Property Urgency As String

        ''' One line a shopkeeper can act on without reading the numbers.
        Public ReadOnly Property Summary As String
            Get
                If AvgDailyDemand <= 0D Then
                    Return $"{OnHand} in stock. No recent sales history, so restock to at least the reorder level ({ReorderLevel})."
                End If
                Dim cover = If(DaysOfCover.HasValue, $"about {DaysOfCover.Value:0.#} day(s) of cover", "no cover")
                Return $"{OnHand} in stock — {cover} at {AvgDailyDemand:0.#}/day. Suggested restock: {SuggestedOrderQty}."
            End Get
        End Property
    End Class

    ''' Demand is averaged over this many trailing days, and an order is sized to
    ''' cover this many days ahead including the wait for delivery.
    Private Const DemandWindowDays As Integer = 90
    Private Const LeadTimeDays As Integer = 7
    Private Const CoverDays As Integer = 30

    Public Function AdviseRestock(productId As Integer) As RestockAdvice
        Dim t = DataAccess.GetTable(
            "SELECT p.Name, p.ReorderLevel, " &
            "  ISNULL((SELECT SUM(QuantityOnHand) FROM StockBatches WHERE ProductID = p.ProductID), 0) AS OnHand, " &
            "  ISNULL((SELECT SUM(Quantity) FROM StockMovements " &
            "          WHERE ProductID = p.ProductID AND MovementType = 'OUT' AND ISNULL(ReferenceType, '') <> 'Transfer' " &
            "            AND MovementDate >= DATEADD(DAY, -@win, SYSDATETIME())), 0) AS SoldInWindow " &
            "FROM Products p WHERE p.ProductID = @p",
            New Dictionary(Of String, Object) From {{"@p", productId}, {"@win", DemandWindowDays}})
        If t.Rows.Count = 0 Then Return Nothing

        Dim row = t.Rows(0)
        Dim onHand = Convert.ToInt32(row("OnHand"))
        Dim reorderLevel = Convert.ToInt32(row("ReorderLevel"))
        Dim sold = Convert.ToInt32(row("SoldInWindow"))
        Dim perDay = Math.Round(sold / CDec(DemandWindowDays), 3)

        ' Safety stock covers half the lead time — a simple, defensible buffer
        ' against demand that arrives faster than the average.
        Dim reorderPoint = CInt(Math.Ceiling(perDay * (LeadTimeDays + LeadTimeDays / 2D)))
        reorderPoint = Math.Max(reorderPoint, reorderLevel)

        Dim target = CInt(Math.Ceiling(perDay * (CoverDays + LeadTimeDays)))
        Dim suggested = Math.Max(0, Math.Max(target, reorderPoint) - onHand)
        If suggested = 0 AndAlso onHand <= reorderPoint Then suggested = Math.Max(1, reorderLevel - onHand)

        Dim cover As Decimal? = If(perDay > 0D, CType(Math.Round(onHand / perDay, 1), Decimal?), Nothing)
        Dim urgency = "OK"
        If onHand <= 0 Then
            urgency = "Out of stock"
        ElseIf onHand <= reorderPoint Then
            urgency = "Reorder now"
        ElseIf cover.HasValue AndAlso cover.Value <= CoverDays Then
            urgency = "Low stock"
        End If

        Return New RestockAdvice() With {
            .ProductID = productId,
            .ProductName = Convert.ToString(row("Name")),
            .OnHand = onHand,
            .ReorderLevel = reorderLevel,
            .AvgDailyDemand = perDay,
            .DaysOfCover = cover,
            .ReorderPoint = reorderPoint,
            .SuggestedOrderQty = suggested,
            .Urgency = urgency}
    End Function

End Module
