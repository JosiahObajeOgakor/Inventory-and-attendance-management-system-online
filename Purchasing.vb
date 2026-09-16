Imports System.Data
Imports System.Data.SqlClient

''' Saving a purchase order: header, lines and the ledger entry for what we now
''' owe the supplier all go in ONE transaction, so a failure part-way leaves no
''' half-written order behind.
'''
''' A supplier we already owe from an earlier order is never quietly skipped:
''' whatever we still owe them is read (locked) before this order is priced,
''' today's order is settled first out of whatever's paid, and anything paid
''' beyond that chips away at the old debt — oldest order first. This mirrors
''' Sales.vb exactly, just with the debt running the other way: a Supplier
''' Credit entry means we owe more, a Debit means we paid — the reverse of a
''' Customer's Debit/Credit.
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
        ''' What's being paid toward the supplier right now — this order first,
        ''' any earlier debt to them after. 0 (the default) matches how this
        ''' always worked before payment-on-order existed.
        Public Property PaidNow As Decimal
        Public Property PaymentMethod As String = "Cash"
    End Class

    Public Class PurchaseResult
        Public Property POID As Integer
        Public Property PONumber As String
        Public Property Subtotal As Decimal
        Public Property VatAmount As Decimal
        Public Property Total As Decimal
        ''' What's left unpaid on THIS order alone — unrelated to any older debt.
        Public Property Outstanding As Decimal
        ''' This order's own status: Paid / Partial / Unpaid.
        Public Property Status As String
        ''' What we already owed this supplier, from earlier orders, before this
        ''' one — read fresh at save time so it can never be stale.
        Public Property PreviousBalance As Decimal
        ''' How much of what was paid went toward that earlier debt rather than
        ''' this order (only ever > 0 once this order is settled first).
        Public Property AppliedToPreviousBalance As Decimal
        ''' What we owe this supplier in total after this order: old debt not
        ''' yet cleared, plus whatever's left unpaid on this order.
        Public Property RemainingBalance As Decimal
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
                ' What we already owed this supplier, read fresh and locked so
                ' two purchases for the same supplier at the same moment can't
                ' both act on a stale figure. This is the "former debt" a new
                ' order must never quietly leave untouched.
                Dim prevBalance = Convert.ToDecimal(DataAccess.ScalarIn(conn, tx,
                    "SELECT Balance FROM Suppliers WITH (UPDLOCK, HOLDLOCK) WHERE SupplierID = @id",
                    New Dictionary(Of String, Object) From {{"@id", req.SupplierID}}))
                money.PreviousBalance = prevBalance

                ' This order is settled first out of whatever is paid; only
                ' what's left over after that goes toward the old debt. Capped
                ' at what's owed in total (old debt + this order) — a payment
                ' can now cover both, not just this order.
                Dim combinedDue = prevBalance + money.Total
                Dim paidNow = Math.Max(0D, Math.Min(req.PaidNow, combinedDue))
                Dim appliedToOrder = Math.Min(paidNow, money.Total)
                Dim overflow = paidNow - appliedToOrder

                money.Outstanding = money.Total - appliedToOrder
                money.Status = If(appliedToOrder >= money.Total, "Paid", If(appliedToOrder > 0, "Partial", "Unpaid"))

                money.PONumber = Numbering.NextNumber(conn, tx, "PO", "PurchaseOrders", "PONumber", req.OrderDate)
                money.POID = DataAccess.InsertReturningId(conn, tx,
                    "INSERT INTO PurchaseOrders (PONumber, SupplierID, OrderDate, TotalAmount, AmountPaid, Status, PaymentStatus, CreatedByUserID) " &
                    "VALUES (@num, @sup, @date, @total, @paid, 'Pending', @status, @user)",
                    New Dictionary(Of String, Object) From {
                        {"@num", money.PONumber}, {"@sup", req.SupplierID}, {"@date", req.OrderDate.Date},
                        {"@total", money.Total}, {"@paid", appliedToOrder}, {"@status", money.Status}, {"@user", userId}})

                For Each line In req.Lines
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO PurchaseOrderItems (POID, ProductID, Quantity, UnitCost) VALUES (@po, @p, @q, @cost)",
                        New Dictionary(Of String, Object) From {
                            {"@po", money.POID}, {"@p", line.ProductID}, {"@q", line.Quantity}, {"@cost", line.UnitCost}})
                Next

                ' Whatever was paid beyond this order pays down what was already
                ' owed — oldest order first, same rule "Mark paid" follows.
                Dim appliedToOldDebt = ApplyPaymentToOutstandingPurchaseOrders(
                    conn, tx, req.SupplierID, overflow, excludeOrderId:=money.POID)
                money.AppliedToPreviousBalance = appliedToOldDebt
                money.RemainingBalance = (prevBalance - appliedToOldDebt) + money.Outstanding

                ' One Balance update for the whole transaction: this order adds
                ' to what we owe, everything just paid (this order and any old
                ' debt) comes off — correct even if two orders race on this supplier.
                DataAccess.Exec(conn, tx, "UPDATE Suppliers SET Balance = Balance - @paid + @total WHERE SupplierID = @id",
                    New Dictionary(Of String, Object) From {{"@paid", paidNow}, {"@total", money.Total}, {"@id", req.SupplierID}})

                ' Supplier ledger polarity is the reverse of a customer's:
                ' Credit = we owe more, Debit = we paid. Only the actual movement
                ' is posted — a fully-paid order posts no Credit at all, matching
                ' how the customer side never posts a Debit for a paid invoice.
                If money.Outstanding > 0 Then
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO Ledger (EntryDate, AccountType, AccountName, EntryType, Amount, Reference) VALUES (@d, 'Supplier', @name, 'Credit', @amt, @ref)",
                        New Dictionary(Of String, Object) From {
                            {"@d", req.OrderDate.Date}, {"@name", req.SupplierName}, {"@amt", money.Outstanding}, {"@ref", money.PONumber}})
                End If
                If appliedToOldDebt > 0 Then
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO Ledger (EntryDate, AccountType, AccountName, EntryType, Amount, Reference) VALUES (@d, 'Supplier', @name, 'Debit', @amt, @ref)",
                        New Dictionary(Of String, Object) From {
                            {"@d", req.OrderDate.Date}, {"@name", req.SupplierName}, {"@amt", appliedToOldDebt}, {"@ref", money.PONumber}})
                End If

                If req.ReceiveNow Then ReceiveInto(conn, tx, req, money, userId)

                Return money
            End Function)
    End Function

    ''' Spreads `amount` across a supplier's outstanding orders, oldest first,
    ''' each capped at what it still owes — so one order can never be marked
    ''' paid past its own total while an older one sits untouched. Returns how
    ''' much was actually applied. Deliberately does NOT touch Suppliers.Balance
    ''' or write a Ledger entry — the caller owns one consistent Balance update
    ''' and Ledger record for its own transaction.
    Public Function ApplyPaymentToOutstandingPurchaseOrders(conn As SqlConnection, tx As SqlTransaction,
                                                            supplierId As Integer, amount As Decimal,
                                                            Optional excludeOrderId As Integer = 0) As Decimal
        If amount <= 0 Then Return 0D
        Dim remaining = amount
        Dim orders = DataAccess.TableIn(conn, tx,
            "SELECT POID, TotalAmount, AmountPaid FROM PurchaseOrders WITH (UPDLOCK, HOLDLOCK) " &
            "WHERE SupplierID = @s AND PaymentStatus <> 'Paid' AND POID <> @ex ORDER BY OrderDate, POID",
            New Dictionary(Of String, Object) From {{"@s", supplierId}, {"@ex", excludeOrderId}})

        For Each row As DataRow In orders.Rows
            If remaining <= 0 Then Exit For
            Dim poId = Convert.ToInt32(row("POID"))
            Dim owed = Convert.ToDecimal(row("TotalAmount")) - Convert.ToDecimal(row("AmountPaid"))
            If owed <= 0 Then Continue For
            Dim apply = Math.Min(owed, remaining)

            DataAccess.Exec(conn, tx,
                "UPDATE PurchaseOrders SET AmountPaid = AmountPaid + @a, " &
                "PaymentStatus = CASE WHEN AmountPaid + @a >= TotalAmount THEN 'Paid' ELSE 'Partial' END WHERE POID = @id",
                New Dictionary(Of String, Object) From {{"@a", apply}, {"@id", poId}})

            remaining -= apply
        Next
        Return amount - remaining
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
