Imports System.Data.SqlClient

''' Saving a sale. Everything a sale touches — the invoice, its lines, the stock
''' coming off the shelf, the stock history, the customer's balance, the ledger,
''' the payment and the rebate — is written in ONE transaction. If anything goes
''' wrong (or the PC dies mid-save) the whole sale is rolled back, so there is
''' never an invoice with no stock deducted, or stock gone with no invoice.
Public Module Sales

    Public Class SaleLine
        Public Property ProductID As Integer
        Public Property ProductName As String
        Public Property Quantity As Integer
        Public Property UnitPrice As Decimal
        Public Property UnitCost As Decimal
        Public ReadOnly Property LineTotal As Decimal
            Get
                Return Quantity * UnitPrice
            End Get
        End Property
    End Class

    Public Class SaleRequest
        Public Property CustomerID As Integer
        Public Property CustomerName As String
        Public Property CustomerType As String
        Public Property SaleDate As Date = Date.Today
        Public Property PriceTier As String = "Retailer"
        Public Property WarehouseID As Integer
        Public Property PaymentMethod As String = "Cash"
        Public Property DiscountPct As Decimal
        ''' 0 when VAT isn't being charged on this sale.
        Public Property VatRate As Decimal
        Public Property PaidNow As Decimal
        Public Property DueDate As Date?
        Public Property Lines As New List(Of SaleLine)
    End Class

    Public Class SaleResult
        Public Property InvoiceID As Integer
        Public Property InvoiceNumber As String
        Public Property Subtotal As Decimal
        Public Property DiscountAmount As Decimal
        Public Property VatAmount As Decimal
        Public Property Total As Decimal
        Public Property Outstanding As Decimal
        Public Property Status As String
    End Class

    ''' One product on a sale that the shelf can't cover, with the restocking
    ''' advice for it already worked out.
    Public Class Shortfall
        Public Property ProductID As Integer
        Public Property ProductName As String
        Public Property Requested As Integer
        Public Property Available As Integer
        Public ReadOnly Property ShortBy As Integer
            Get
                Return Math.Max(0, Requested - Available)
            End Get
        End Property
        Public Property Advice As Stock.RestockAdvice
    End Class

    ''' Raised instead of saving a sale that would take more off the shelf than
    ''' is on it. Carries every short line so the seller can fix the whole order
    ''' in one go rather than one product at a time.
    Public Class InsufficientStockException
        Inherits InvalidOperationException

        Public ReadOnly Property Shortfalls As List(Of Shortfall)

        Public Sub New(items As List(Of Shortfall))
            MyBase.New(BuildMessage(items))
            Shortfalls = items
        End Sub

        Private Shared Function BuildMessage(items As List(Of Shortfall)) As String
            Dim sb As New Text.StringBuilder()
            sb.AppendLine("Not enough stock to save this sale:")
            sb.AppendLine()
            For Each item In items
                sb.AppendLine($"• {item.ProductName} — asked for {item.Requested}, only {item.Available} in stock (short {item.ShortBy}).")
                If item.Advice IsNot Nothing Then sb.AppendLine($"   Restock: {item.Advice.Summary}")
            Next
            sb.AppendLine()
            sb.Append("Reduce the quantities, or record the purchase/production that brings the stock in first.")
            Return sb.ToString()
        End Function
    End Class

    ''' Checks a sale against what's on the shelf without saving anything — used
    ''' by the sale screen so the seller is told before they hit Save.
    Public Function FindShortfalls(req As SaleRequest) As List(Of Shortfall)
        Dim shortfalls As New List(Of Shortfall)
        For Each grp In req.Lines.GroupBy(Function(l) l.ProductID)
            Dim wanted = grp.Sum(Function(l) l.Quantity)
            Dim have = Stock.AvailabilityFor(grp.Key, req.WarehouseID)
            If wanted > have.Total Then
                shortfalls.Add(New Shortfall() With {
                    .ProductID = grp.Key,
                    .ProductName = If(have.ProductName, grp.First().ProductName),
                    .Requested = wanted,
                    .Available = have.Total,
                    .Advice = Stock.AdviseRestock(grp.Key)})
            End If
        Next
        Return shortfalls
    End Function

    ''' Works out the money for a sale without touching the database.
    Public Function Totals(req As SaleRequest) As SaleResult
        Dim r As New SaleResult()
        r.Subtotal = req.Lines.Sum(Function(l) l.LineTotal)
        r.DiscountAmount = Math.Round(r.Subtotal * req.DiscountPct / 100D, 2)
        r.VatAmount = Math.Round((r.Subtotal - r.DiscountAmount) * req.VatRate / 100D, 2)
        r.Total = r.Subtotal - r.DiscountAmount + r.VatAmount
        Dim paid = Math.Min(req.PaidNow, r.Total)
        r.Outstanding = r.Total - paid
        r.Status = If(r.Outstanding <= 0, "Paid", If(paid > 0, "Partial", "Unpaid"))
        Return r
    End Function

    ''' Saves the sale. Throws if anything fails — nothing is left behind.
    Public Function Save(req As SaleRequest, userId As Integer) As SaleResult
        If req.Lines.Count = 0 Then Throw New InvalidOperationException("Add at least one product line.")

        Dim money = Totals(req)
        Dim paidNow = Math.Min(req.PaidNow, money.Total)

        ' The invoice number is stamped with the current second, so two sales
        ' saved within the same second would clash — wait a full second past
        ' the clash so the retry lands in the next second and gets its own number.
        For attempt = 1 To 4
            Try
                Return SaveOnce(req, userId, money, paidNow)
            Catch ex As SqlException When attempt < 4 AndAlso Numbering.IsDuplicate(ex)
                Threading.Thread.Sleep(1000)
            End Try
        Next
        Return SaveOnce(req, userId, money, paidNow)
    End Function

    Private Function SaveOnce(req As SaleRequest, userId As Integer, money As SaleResult, paidNow As Decimal) As SaleResult
        Return DataAccess.InTransaction(
            Function(conn As SqlConnection, tx As SqlTransaction) As SaleResult
                ' Locked read of every product on the order before anything is
                ' written, so a second till can't sell the same units at the
                ' same moment and leave both invoices half-covered.
                AssertStockAvailable(conn, tx, req)

                money.InvoiceNumber = Numbering.NextNumber(conn, tx, "INV", "Invoices", "InvoiceNumber", req.SaleDate)
                money.InvoiceID = DataAccess.InsertReturningId(conn, tx,
                    "INSERT INTO Invoices (InvoiceNumber, CustomerID, InvoiceDate, Subtotal, DiscountPct, DiscountAmount, VATRate, VATAmount, " &
                    "TotalAmount, AmountPaid, DueDate, PriceTier, WarehouseID, PaymentMethod, [Status], CreatedByUserID) " &
                    "VALUES (@num, @cust, @date, @sub, @discPct, @disc, @vatRate, @vat, @total, @paid, @due, @tier, @wh, @method, @status, @user)",
                    New Dictionary(Of String, Object) From {
                        {"@num", money.InvoiceNumber}, {"@cust", req.CustomerID}, {"@date", req.SaleDate.Date},
                        {"@sub", money.Subtotal}, {"@discPct", req.DiscountPct}, {"@disc", money.DiscountAmount},
                        {"@vatRate", req.VatRate}, {"@vat", money.VatAmount}, {"@total", money.Total}, {"@paid", paidNow},
                        {"@due", If(money.Outstanding > 0 AndAlso req.DueDate.HasValue, CObj(req.DueDate.Value.Date), DBNull.Value)},
                        {"@tier", req.PriceTier}, {"@wh", req.WarehouseID}, {"@method", req.PaymentMethod},
                        {"@status", money.Status}, {"@user", userId}})

                For Each line In req.Lines
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO InvoiceItems (InvoiceID, ProductID, Quantity, UnitPrice, UnitCost, LineTotal) VALUES (@i, @p, @q, @price, @cost, @lt)",
                        New Dictionary(Of String, Object) From {
                            {"@i", money.InvoiceID}, {"@p", line.ProductID}, {"@q", line.Quantity},
                            {"@price", line.UnitPrice}, {"@cost", line.UnitCost}, {"@lt", line.LineTotal}})

                    DeductStock(conn, tx, line, req.WarehouseID, money.InvoiceID, req.SaleDate, userId)
                Next

                If money.Outstanding > 0 Then
                    DataAccess.Exec(conn, tx, "UPDATE Customers SET Balance = Balance + @amt WHERE CustomerID = @id",
                        New Dictionary(Of String, Object) From {{"@amt", money.Outstanding}, {"@id", req.CustomerID}})
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO Ledger (EntryDate, AccountType, AccountName, EntryType, Amount, Reference) VALUES (@d, 'Customer', @name, 'Debit', @amt, @ref)",
                        New Dictionary(Of String, Object) From {
                            {"@d", req.SaleDate.Date}, {"@name", req.CustomerName}, {"@amt", money.Outstanding}, {"@ref", money.InvoiceNumber}})
                End If

                If paidNow > 0 Then
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO Payments (InvoiceID, PaymentDate, Amount, Method, ReceivedByUserID) VALUES (@i, @d, @a, @m, @u)",
                        New Dictionary(Of String, Object) From {
                            {"@i", money.InvoiceID}, {"@d", req.SaleDate.Date}, {"@a", paidNow}, {"@m", req.PaymentMethod}, {"@u", userId}})
                End If

                ' Rebate accrues on net sales for everyone except walk-ins.
                If Not String.Equals(req.CustomerType, "Walk-in", StringComparison.OrdinalIgnoreCase) Then
                    Dim rate = Convert.ToDecimal(DataAccess.ScalarIn(conn, tx,
                        "SELECT ISNULL(RebateRatePct, 0) FROM Customers WHERE CustomerID = @id",
                        New Dictionary(Of String, Object) From {{"@id", req.CustomerID}}))
                    Dim netSales = money.Total - money.VatAmount
                    Dim rebate = Math.Round(netSales * rate / 100D, 2)
                    If rebate > 0 Then
                        DataAccess.Exec(conn, tx,
                            "INSERT INTO RebateEntries (CustomerID, InvoiceID, EntryDate, Amount, [Status], Note) VALUES (@c, @i, @d, @a, 'Accrued', @n)",
                            New Dictionary(Of String, Object) From {
                                {"@c", req.CustomerID}, {"@i", money.InvoiceID}, {"@d", req.SaleDate.Date}, {"@a", rebate},
                                {"@n", $"{rate}% of {AppInfo.Money(netSales)} — {money.InvoiceNumber}"}})
                    End If
                End If

                Return money
            End Function)
    End Function

    ''' Locks and totals every product on the order, and refuses the whole sale
    ''' if any line asks for more than the shelf holds. Runs before the first
    ''' write, so a refused sale leaves nothing behind.
    Private Sub AssertStockAvailable(conn As SqlConnection, tx As SqlTransaction, req As SaleRequest)
        Dim shortfalls As New List(Of Shortfall)

        For Each grp In req.Lines.GroupBy(Function(l) l.ProductID)
            Dim wanted = grp.Sum(Function(l) l.Quantity)
            Dim have = Convert.ToInt32(DataAccess.ScalarIn(conn, tx,
                "SELECT ISNULL(SUM(QuantityOnHand), 0) FROM StockBatches WITH (UPDLOCK, HOLDLOCK) WHERE ProductID = @p",
                New Dictionary(Of String, Object) From {{"@p", grp.Key}}))
            If wanted > have Then
                shortfalls.Add(New Shortfall() With {
                    .ProductID = grp.Key,
                    .ProductName = grp.First().ProductName,
                    .Requested = wanted,
                    .Available = have,
                    .Advice = Stock.AdviseRestock(grp.Key)})
            End If
        Next

        If shortfalls.Count > 0 Then Throw New InsufficientStockException(shortfalls)
    End Sub

    ''' Takes one line off the shelf, emptying the chosen warehouse's batches
    ''' first and oldest-expiry-first within each, so short-dated goods move
    ''' before fresh ones. Splits across batches when no single batch covers the
    ''' line, and refuses to leave a batch negative.
    Private Sub DeductStock(conn As SqlConnection, tx As SqlTransaction, line As SaleLine,
                            warehouseId As Integer, invoiceId As Integer, saleDate As Date, userId As Integer)
        Dim batches = DataAccess.TableIn(conn, tx,
            "SELECT BatchID, WarehouseID, QuantityOnHand FROM StockBatches WITH (UPDLOCK, HOLDLOCK) " &
            "WHERE ProductID = @p AND QuantityOnHand > 0 " &
            "ORDER BY CASE WHEN WarehouseID = @w THEN 0 ELSE 1 END, " &
            "         CASE WHEN ExpiryDate IS NULL THEN 1 ELSE 0 END, ExpiryDate, BatchID",
            New Dictionary(Of String, Object) From {{"@p", line.ProductID}, {"@w", warehouseId}})

        Dim remaining = line.Quantity
        For Each b As DataRow In batches.Rows
            If remaining <= 0 Then Exit For
            Dim onHand = Convert.ToInt32(b("QuantityOnHand"))
            Dim take = Math.Min(onHand, remaining)

            DataAccess.Exec(conn, tx,
                "UPDATE StockBatches SET QuantityOnHand = QuantityOnHand - @take WHERE BatchID = @id AND QuantityOnHand >= @take",
                New Dictionary(Of String, Object) From {{"@take", take}, {"@id", Convert.ToInt32(b("BatchID"))}})

            ' Logged against the warehouse the units actually left.
            DataAccess.Exec(conn, tx,
                "INSERT INTO StockMovements (ProductID, WarehouseID, MovementType, Quantity, ReferenceType, ReferenceID, MovementDate, UserID) " &
                "VALUES (@p, @w, 'OUT', @q, 'Invoice', @i, @d, @u)",
                New Dictionary(Of String, Object) From {
                    {"@p", line.ProductID}, {"@w", Convert.ToInt32(b("WarehouseID"))}, {"@q", take},
                    {"@i", invoiceId}, {"@d", saleDate.Date}, {"@u", userId}})

            remaining -= take
        Next

        ' AssertStockAvailable already cleared this line under the same lock, so
        ' anything left here means the shelf changed underneath us — refuse.
        If remaining > 0 Then
            Throw New InsufficientStockException(New List(Of Shortfall) From {
                New Shortfall() With {
                    .ProductID = line.ProductID,
                    .ProductName = line.ProductName,
                    .Requested = line.Quantity,
                    .Available = line.Quantity - remaining,
                    .Advice = Stock.AdviseRestock(line.ProductID)}})
        End If
    End Sub

End Module
