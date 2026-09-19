Imports System.Data
Imports System.Data.SqlClient

''' Saving a quotation. Unlike Sales.Save, this never touches stock, the
''' ledger or a customer's balance — it's a price computation a customer can
''' walk away from with nothing left behind. A quotation only affects any of
''' that once it's converted into a real sale, which runs through the
''' ordinary Sales.Save transaction and then calls ConvertToSale to stamp this
''' quotation as spent.
Public Module Quotations

    Public Class QuoteLine
        Public Property ProductID As Integer
        Public Property ProductName As String
        Public Property Quantity As Integer
        Public Property UnitPrice As Decimal
        Public ReadOnly Property LineTotal As Decimal
            Get
                Return Quantity * UnitPrice
            End Get
        End Property
    End Class

    Public Class QuoteRequest
        Public Property CustomerID As Integer
        Public Property QuotationDate As Date = Date.Today
        Public Property PriceTier As String = "Retailer"
        Public Property DiscountPct As Decimal
        Public Property VatRate As Decimal
        Public Property Lines As New List(Of QuoteLine)
    End Class

    Public Class QuoteResult
        Public Property QuotationID As Integer
        Public Property QuotationNumber As String
        Public Property Subtotal As Decimal
        Public Property DiscountAmount As Decimal
        Public Property VatAmount As Decimal
        Public Property Total As Decimal
    End Class

    ''' Works out the money for a quotation without touching the database.
    Public Function Totals(req As QuoteRequest) As QuoteResult
        Dim r As New QuoteResult()
        r.Subtotal = req.Lines.Sum(Function(l) l.LineTotal)
        r.DiscountAmount = Math.Round(r.Subtotal * req.DiscountPct / 100D, 2)
        r.VatAmount = Math.Round((r.Subtotal - r.DiscountAmount) * req.VatRate / 100D, 2)
        r.Total = r.Subtotal - r.DiscountAmount + r.VatAmount
        Return r
    End Function

    ''' Saves the quotation. Any line priced away from that product's own
    ''' tier price (looked up fresh here, same source frmNewQuotation prices
    ''' from) is logged to PriceOverrides in the same transaction.
    Public Function Save(req As QuoteRequest, userId As Integer) As QuoteResult
        If req.Lines.Count = 0 Then Throw New InvalidOperationException("Add at least one product line.")
        Dim money = Totals(req)

        For attempt = 1 To 4
            Try
                Return SaveOnce(req, userId, money)
            Catch ex As SqlException When attempt < 4 AndAlso Numbering.IsDuplicate(ex)
                Threading.Thread.Sleep(1000)
            End Try
        Next
        Return SaveOnce(req, userId, money)
    End Function

    Private Function SaveOnce(req As QuoteRequest, userId As Integer, money As QuoteResult) As QuoteResult
        Return DataAccess.InTransaction(
            Function(conn As SqlConnection, tx As SqlTransaction) As QuoteResult
                Dim changedByName = Convert.ToString(DataAccess.ScalarIn(conn, tx,
                    "SELECT FullName FROM Users WHERE UserID = @id", New Dictionary(Of String, Object) From {{"@id", userId}}))
                money.QuotationNumber = Numbering.NextNumber(conn, tx, "QUO", "Quotations", "QuotationNumber", req.QuotationDate)
                money.QuotationID = DataAccess.InsertReturningId(conn, tx,
                    "INSERT INTO Quotations (QuotationNumber, CustomerID, QuotationDate, Subtotal, DiscountPct, DiscountAmount, " &
                    "VATRate, VATAmount, TotalAmount, PriceTier, CreatedByUserID) " &
                    "VALUES (@num, @cust, @date, @sub, @discPct, @disc, @vatRate, @vat, @total, @tier, @user)",
                    New Dictionary(Of String, Object) From {
                        {"@num", money.QuotationNumber}, {"@cust", req.CustomerID}, {"@date", req.QuotationDate.Date},
                        {"@sub", money.Subtotal}, {"@discPct", req.DiscountPct}, {"@disc", money.DiscountAmount},
                        {"@vatRate", req.VatRate}, {"@vat", money.VatAmount}, {"@total", money.Total}, {"@tier", req.PriceTier},
                        {"@user", userId}})

                For Each line In req.Lines
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO QuotationItems (QuotationID, ProductID, Quantity, UnitPrice, LineTotal) VALUES (@q, @p, @qty, @price, @lt)",
                        New Dictionary(Of String, Object) From {
                            {"@q", money.QuotationID}, {"@p", line.ProductID}, {"@qty", line.Quantity},
                            {"@price", line.UnitPrice}, {"@lt", line.LineTotal}})

                    Dim standard = TierPrice(conn, tx, line.ProductID, req.PriceTier)
                    If standard.HasValue AndAlso standard.Value <> line.UnitPrice Then
                        PriceOverrides.Log(conn, tx, "Quotation", money.QuotationNumber, line.ProductName,
                                            standard.Value, line.UnitPrice, changedByName)
                    End If
                Next

                Return money
            End Function)
    End Function

    Private Function TierPrice(conn As SqlConnection, tx As SqlTransaction, productId As Integer, tier As String) As Decimal?
        Dim col As String
        Select Case tier
            Case "Distributor" : col = "PriceDistributor"
            Case "Wholesaler" : col = "PriceWholesaler"
            Case Else : col = "PriceRetail"
        End Select
        Dim t = DataAccess.TableIn(conn, tx, $"SELECT {col} AS P FROM Products WHERE ProductID = @id",
            New Dictionary(Of String, Object) From {{"@id", productId}})
        If t.Rows.Count = 0 Then Return Nothing
        Return Convert.ToDecimal(t.Rows(0)("P"))
    End Function

    ''' Marks a quotation as spent once its sale has actually saved. Never
    ''' call this before Sales.Save succeeds — a failed sale must leave the
    ''' quotation exactly as it was.
    Public Sub ConvertToSale(quotationId As Integer, invoiceId As Integer)
        DataAccess.Execute("UPDATE Quotations SET Status = 'Converted', ConvertedInvoiceID = @inv WHERE QuotationID = @q",
            New Dictionary(Of String, Object) From {{"@inv", invoiceId}, {"@q", quotationId}})
    End Sub

    ''' Deletes a quotation and its lines. Safe at any time, Open or
    ''' Converted — nothing else references a quotation row, and any
    ''' PriceOverrides history for it already stands on its own.
    Public Sub Delete(quotationId As Integer)
        DataAccess.ExecuteTransaction(New List(Of (Sql As String, Params As Dictionary(Of String, Object))) From {
            ("DELETE FROM QuotationItems WHERE QuotationID = @id", New Dictionary(Of String, Object) From {{"@id", quotationId}}),
            ("DELETE FROM Quotations WHERE QuotationID = @id", New Dictionary(Of String, Object) From {{"@id", quotationId}})
        })
    End Sub

End Module
