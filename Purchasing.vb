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

                Return money
            End Function)
    End Function

End Module
