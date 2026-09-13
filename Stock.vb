Imports System.Data

''' Stock movements that aren't part of a sale or purchase — currently the
''' production entries staff record after each production run.
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
                "INSERT INTO StockMovements (ProductID, WarehouseID, MovementType, Quantity, ReferenceType, MovementDate, UserID) " &
                "  VALUES (@p, @w, 'IN', @q, 'Production', @d, @u); " &
                "COMMIT;", p)
            Return ""
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
            "          WHERE ProductID = p.ProductID AND MovementType = 'OUT' " &
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
