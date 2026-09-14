Imports System.Data.SqlClient

''' Saving a purchase order: header, lines and the ledger entry for what we now
''' owe the supplier all go in ONE transaction, so a failure part-way leaves no
''' half-written order behind.
Public Module Purchasing

    Public Class PurchaseLine
        Public Property ProductID As Integer
        Public Property ProductName As String
        Public Property Quantity As Integer
        Public Property UnitCost As Decimal
        Public ReadOnly Property LineTotal As Decimal
            Get
                Return Quantity * UnitCost
            End Get
        End Property
    End Class

    Public Class PurchaseRequest
        Public Property SupplierID As Integer
        Public Property SupplierName As String
        Public Property OrderDate As Date = Date.Today
        ''' 0 when VAT isn't being added to this purchase.
        Public Property VatRate As Decimal
        Public Property Lines As New List(Of PurchaseLine)
        ''' The goods are already here: book them into stock as part of saving.
        ''' Candid Purrfect buys rather than produces, so its purchases fill the
        ''' shelves directly. False keeps the order-now, receive-later flow.
        Public Property ReceiveNow As Boolean
        ''' Where received goods go; 0 = the first warehouse.
        Public Property WarehouseID As Integer
    End Class

    Public Class PurchaseResult
        Public Property POID As Integer
        Public Property PONumber As String
        Public Property Subtotal As Decimal
        Public Property VatAmount As Decimal
        Public Property Total As Decimal
    End Class

    Public Function Totals(req As PurchaseRequest) As PurchaseResult
        Dim r As New PurchaseResult()
        r.Subtotal = req.Lines.Sum(Function(l) l.LineTotal)
        r.VatAmount = Math.Round(r.Subtotal * req.VatRate / 100D, 2)
        r.Total = r.Subtotal + r.VatAmount
        Return r
    End Function

    ''' Saves the purchase order. Throws if anything fails — nothing is left behind.
    Public Function Save(req As PurchaseRequest, userId As Integer) As PurchaseResult
        If req.Lines.Count = 0 Then Throw New InvalidOperationException("Add at least one line item.")

        Dim money = Totals(req)

        ' The PO number is stamped with the current second, so two orders saved
        ' within the same second would clash — wait a full second past the clash
        ' so the retry lands in the next second and gets its own number.
        For attempt = 1 To 4
            Try
                Return SaveOnce(req, userId, money)
            Catch ex As SqlException When attempt < 4 AndAlso Numbering.IsDuplicate(ex)
                Threading.Thread.Sleep(1000)
            End Try
        Next
        Return SaveOnce(req, userId, money)
    End Function

    Private Function SaveOnce(req As PurchaseRequest, userId As Integer, money As PurchaseResult) As PurchaseResult
        Return DataAccess.InTransaction(
            Function(conn As SqlConnection, tx As SqlTransaction) As PurchaseResult
                money.PONumber = Numbering.NextNumber(conn, tx, "PO", "PurchaseOrders", "PONumber", req.OrderDate)
                money.POID = DataAccess.InsertReturningId(conn, tx,
                    "INSERT INTO PurchaseOrders (PONumber, SupplierID, OrderDate, TotalAmount, Status, PaymentStatus, CreatedByUserID) " &
                    "VALUES (@num, @sup, @date, @total, 'Pending', 'Unpaid', @user)",
                    New Dictionary(Of String, Object) From {
                        {"@num", money.PONumber}, {"@sup", req.SupplierID}, {"@date", req.OrderDate.Date},
                        {"@total", money.Total}, {"@user", userId}})

                For Each line In req.Lines
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO PurchaseOrderItems (POID, ProductID, Quantity, UnitCost) VALUES (@po, @p, @q, @cost)",
                        New Dictionary(Of String, Object) From {
                            {"@po", money.POID}, {"@p", line.ProductID}, {"@q", line.Quantity}, {"@cost", line.UnitCost}})
                Next

                DataAccess.Exec(conn, tx,
                    "INSERT INTO Ledger (EntryDate, AccountType, AccountName, EntryType, Amount, Reference) VALUES (@d, 'Supplier', @name, 'Credit', @amt, @ref)",
                    New Dictionary(Of String, Object) From {
                        {"@d", req.OrderDate.Date}, {"@name", req.SupplierName}, {"@amt", money.Total}, {"@ref", money.PONumber}})

                If req.ReceiveNow Then ReceiveInto(conn, tx, req, money, userId)

                Return money
            End Function)
    End Function

    ''' Books a purchase's goods into stock inside the saving transaction, so an
    ''' order can never exist without its stock or its stock without the order.
    ''' Each purchase gets its own batch (named by its number, so any unit on the
    ''' shelf traces back to the purchase that brought it in) and a stock-history
    ''' line. The product's cost becomes what was just paid, keeping profit on the
    ''' next sale honest, and an unlabelled product gets its barcode now.
    Private Sub ReceiveInto(conn As SqlConnection, tx As SqlTransaction, req As PurchaseRequest,
                            money As PurchaseResult, userId As Integer)
        Dim warehouseId = req.WarehouseID
        If warehouseId <= 0 Then
            warehouseId = Convert.ToInt32(DataAccess.ScalarIn(conn, tx, "SELECT MIN(WarehouseID) FROM Warehouses"))
        End If
        For Each line In req.Lines
            DataAccess.Exec(conn, tx,
                "UPDATE StockBatches SET QuantityOnHand = QuantityOnHand + @q WHERE ProductID = @p AND WarehouseID = @w AND BatchNumber = @b; " &
                "IF @@ROWCOUNT = 0 INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p, @w, @b, @q); " &
                "INSERT INTO StockMovements (ProductID, WarehouseID, MovementType, Quantity, ReferenceType, ReferenceID, MovementDate, UserID) " &
                "  VALUES (@p, @w, 'IN', @q, 'PurchaseOrder', @po, @d, @u); " &
                "UPDATE Products SET CostPrice = CASE WHEN @cost > 0 THEN @cost ELSE CostPrice END, " &
                "  Barcode = CASE WHEN Barcode IS NULL OR Barcode = '' THEN @bc ELSE Barcode END WHERE ProductID = @p;",
                New Dictionary(Of String, Object) From {
                    {"@p", line.ProductID}, {"@w", warehouseId}, {"@q", line.Quantity}, {"@b", money.PONumber},
                    {"@po", money.POID}, {"@d", req.OrderDate.Date}, {"@u", userId},
                    {"@cost", line.UnitCost}, {"@bc", Barcodes.MintInternalBarcode(line.ProductID)}})
        Next
        DataAccess.Exec(conn, tx, "UPDATE PurchaseOrders SET Status = 'Received' WHERE POID = @po",
                        New Dictionary(Of String, Object) From {{"@po", money.POID}})
    End Sub

End Module
