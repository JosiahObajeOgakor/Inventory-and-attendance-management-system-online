Imports System.Data
Imports System.Data.SqlClient

''' Builds a few months of believable trading history — sales, purchase orders
''' and stock movements — so the forecasting, market-basket and anomaly work has
''' something real to read.
'''
''' Why this exists: every one of those algorithms is silent on an empty
''' database, and silence looks identical to "broken". Six weeks of sales is the
''' minimum before a forecast means anything and a basket rule can clear its
''' support threshold, so a new install has no way to show what it does.
'''
''' Two rules it sticks to:
'''   • Deterministic. Same seed, same history, every time — so a demo, a
'''     screenshot and a test all show the same numbers.
'''   • Never silently. It refuses to run on a database that already holds real
'''     sales, and everything it writes is tagged in a way that can be found
'''     again and removed.
Public Module SampleData

    ''' True when this database has trading history of its own. The generator
    ''' will not touch it — mixing invented sales into real books would corrupt
    ''' every report the business relies on.
    Public Function HasRealHistory() As Boolean
        Return Convert.ToInt32(DataAccess.GetTable(
            "SELECT COUNT(*) FROM Invoices WHERE IsSample = 0").Rows(0)(0)) > 0
    End Function

    ''' How many generated invoices are on file.
    Public Function SampleInvoiceCount() As Integer
        Return Convert.ToInt32(DataAccess.GetTable(
            "SELECT COUNT(*) FROM Invoices WHERE IsSample = 1").Rows(0)(0))
    End Function

    ''' Generates `weeks` of sales history. Returns how many invoices were
    ''' written, or throws if the database already has real trading on it.
    '''
    ''' The pattern is built deliberately, not sprinkled at random:
    '''   • two products that genuinely travel together, so market basket has a
    '''     real rule to find rather than a coincidence;
    '''   • a rising weekly trend, so the forecast has a direction to detect;
    '''   • one oversized movement, so the anomaly detector has a true positive.
    ''' Anything the algorithms then report can be checked against what was
    ''' planted, which is the only way to know they work.
    Public Function Generate(userId As Integer, Optional weeks As Integer = 10, Optional seed As Integer = 20260913) As Integer
        If HasRealHistory() Then
            Throw New InvalidOperationException(
                "This database already holds real sales. Sample data is only for a fresh install.")
        End If

        Dim rng As New Trees.DeterministicRandom(seed)
        Dim products = DataAccess.GetTable("SELECT TOP 6 ProductID, Name, CostPrice, PriceRetail FROM Products WHERE IsActive = 1 ORDER BY ProductID")
        Dim customers = DataAccess.GetTable("SELECT TOP 5 CustomerID FROM Customers ORDER BY CustomerID")
        If products.Rows.Count < 2 OrElse customers.Rows.Count = 0 Then Return 0

        Dim warehouseId = Convert.ToInt32(DataAccess.GetTable("SELECT TOP 1 WarehouseID FROM Warehouses ORDER BY WarehouseID").Rows(0)(0))

        ' The pair that travels together: whenever the first goes out, the
        ' second usually does too. That is the rule the miner should find.
        Dim anchor = Convert.ToInt32(products.Rows(0)("ProductID"))
        Dim partner = Convert.ToInt32(products.Rows(1)("ProductID"))

        ' The whole build runs as one transaction. Row by row it's several
        ' hundred separate round trips — slow enough to be felt on the screen
        ' that offers this, and slow enough to make the tests that rely on it
        ' unusable. It also means a failure part-way leaves nothing behind.
        Return DataAccess.InTransaction(
            Function(conn, tx) As Integer
                StockTheShelves(conn, tx, products, warehouseId)
                Dim count = WriteSales(conn, tx, userId, weeks, rng, products, customers, anchor, partner)
                ' One movement far outside the pattern, for the anomaly detector.
                WriteOversizedMovement(conn, tx, anchor, warehouseId, userId)
                WritePurchaseHistory(conn, tx, userId, products, rng)
                Return count
            End Function)
    End Function

    Private Function WriteSales(conn As SqlConnection, tx As SqlTransaction,
                                userId As Integer, weeks As Integer, rng As Trees.DeterministicRandom,
                                products As DataTable, customers As DataTable,
                                anchor As Integer, partner As Integer) As Integer
        Dim written = 0
        For week = weeks To 1 Step -1
            ' Sales climb gently as the weeks come forward, giving the forecast a
            ' trend rather than a flat line.
            Dim salesThisWeek = 3 + (weeks - week) \ 3
            For sale = 1 To salesThisWeek
                Dim soldOn = Date.Today.AddDays(-(week * 7) + (sale Mod 5))
                Dim customerId = Convert.ToInt32(customers.Rows(rng.NextInt(customers.Rows.Count))("CustomerID"))

                ' The pairing has to be genuinely informative, not merely common.
                ' A product in every basket predicts nothing — lift divides the
                ' confidence by how popular the product already is, so putting
                ' the anchor on every sale would give a rule of lift exactly 1
                ' that the miner is right to ignore. So plenty of baskets carry
                ' neither of the pair:
                '   ~50% both · ~5% anchor alone · ~5% partner alone · ~40% neither
                ' which leaves P(both) well above P(anchor) x P(partner).
                Dim lines As New List(Of (ProductID As Integer, Qty As Integer, Price As Decimal))
                Dim roll = rng.NextInt(100)
                If roll < 55 Then
                    lines.Add((anchor, 2 + rng.NextInt(4), Convert.ToDecimal(products.Rows(0)("PriceRetail"))))
                    lines.Add((partner, 1 + rng.NextInt(3), Convert.ToDecimal(products.Rows(1)("PriceRetail"))))
                ElseIf roll < 60 Then
                    lines.Add((anchor, 2 + rng.NextInt(4), Convert.ToDecimal(products.Rows(0)("PriceRetail"))))
                ElseIf roll < 65 Then
                    lines.Add((partner, 1 + rng.NextInt(3), Convert.ToDecimal(products.Rows(1)("PriceRetail"))))
                ElseIf products.Rows.Count > 2 Then
                    ' Neither of the pair — the noise the rule has to be found in.
                    Dim other = 2 + rng.NextInt(products.Rows.Count - 2)
                    lines.Add((Convert.ToInt32(products.Rows(other)("ProductID")), 1 + rng.NextInt(3),
                               Convert.ToDecimal(products.Rows(other)("PriceRetail"))))
                Else
                    lines.Add((anchor, 2, Convert.ToDecimal(products.Rows(0)("PriceRetail"))))
                End If

                WriteInvoice(conn, tx, userId, customerId, soldOn, lines)
                written += 1
            Next
        Next
        Return written
    End Function

    ''' Enough stock that the generated sales don't drive quantities negative.
    Private Sub StockTheShelves(conn As SqlConnection, tx As SqlTransaction,
                                products As DataTable, warehouseId As Integer)
        For Each p As DataRow In products.Rows
            DataAccess.Exec(conn, tx,
                "IF NOT EXISTS (SELECT 1 FROM StockBatches WHERE ProductID=@p AND WarehouseID=@w AND BatchNumber=@b) " &
                "INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p, @w, @b, 400)",
                New Dictionary(Of String, Object) From {
                    {"@p", Convert.ToInt32(p("ProductID"))}, {"@w", warehouseId}, {"@b", "SAMPLE"}})
        Next
    End Sub

    Private Sub WriteInvoice(conn As SqlConnection, tx As SqlTransaction,
                             userId As Integer, customerId As Integer, soldOn As Date,
                             lines As List(Of (ProductID As Integer, Qty As Integer, Price As Decimal)))
        Dim total = lines.Sum(Function(l) l.Qty * l.Price)
        Dim number = $"ChewyStock-{soldOn:ddMMyyyy}-{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant()}"

        Dim invoiceId = DataAccess.InsertReturningId(conn, tx,
            "INSERT INTO Invoices (InvoiceNumber, CustomerID, InvoiceDate, Subtotal, TotalAmount, AmountPaid, " &
            "VATRate, VATAmount, [Status], IsSample, CreatedByUserID) " &
            "VALUES (@num, @c, @d, @t, @t, @t, 0, 0, 'Paid', 1, @u)",
            New Dictionary(Of String, Object) From {
                {"@num", number}, {"@c", customerId}, {"@d", soldOn}, {"@t", total}, {"@u", userId}})

        For Each line In lines
            DataAccess.Exec(conn, tx,
                "INSERT INTO InvoiceItems (InvoiceID, ProductID, Quantity, UnitPrice, UnitCost, LineTotal) " &
                "VALUES (@i, @p, @q, @price, 0, @lineTotal)",
                New Dictionary(Of String, Object) From {
                    {"@i", invoiceId}, {"@p", line.ProductID}, {"@q", line.Qty},
                    {"@price", line.Price}, {"@lineTotal", line.Qty * line.Price}})
        Next
    End Sub

    ''' A single movement an order of magnitude past the rest — a planted true
    ''' positive, so "the detector found nothing" can be told apart from "the
    ''' detector isn't running".
    Private Sub WriteOversizedMovement(conn As SqlConnection, tx As SqlTransaction,
                                       productId As Integer, warehouseId As Integer, userId As Integer)
        DataAccess.Exec(conn, tx,
            "INSERT INTO StockMovements (ProductID, WarehouseID, MovementType, Quantity, MovementDate, ReferenceType, UserID) " &
            "VALUES (@p, @w, 'IN', 4000, @d, 'Production', @u)",
            New Dictionary(Of String, Object) From {
                {"@p", productId}, {"@w", warehouseId}, {"@d", Date.Today.AddDays(-9)}, {"@u", userId}})
    End Sub

    ''' Past purchase orders, so the buying screen has a supplier history to
    ''' forecast against and a normal unit cost to judge a new one by.
    Private Sub WritePurchaseHistory(conn As SqlConnection, tx As SqlTransaction,
                                     userId As Integer, products As DataTable,
                                     rng As Trees.DeterministicRandom)
        Dim suppliers = DataAccess.TableIn(conn, tx, "SELECT TOP 2 SupplierID FROM Suppliers ORDER BY SupplierID")
        If suppliers.Rows.Count = 0 Then Return

        For Each s As DataRow In suppliers.Rows
            Dim supplierId = Convert.ToInt32(s("SupplierID"))
            For order = 1 To 10
                Dim orderedOn = Date.Today.AddDays(-(order * 12))
                Dim poId = DataAccess.InsertReturningId(conn, tx,
                    "INSERT INTO PurchaseOrders (PONumber, SupplierID, OrderDate, TotalAmount, Status, PaymentStatus, IsSample, CreatedByUserID) " &
                    "VALUES (@num, @s, @d, 0, 'Received', 'Paid', 1, @u)",
                    New Dictionary(Of String, Object) From {
                        {"@num", $"ChewyStock-{orderedOn:ddMMyyyy}-{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant()}"},
                        {"@s", supplierId}, {"@d", orderedOn}, {"@u", userId}})

                Dim total As Decimal = 0
                For Each p As DataRow In products.Rows
                    ' Costs wobble a few percent around the product's own cost —
                    ' a believable spread, so a genuinely odd price stands out.
                    Dim baseCost = Convert.ToDecimal(p("CostPrice"))
                    Dim cost = Math.Round(baseCost * (0.95D + rng.NextInt(10) / 100D), 2)
                    Dim qty = 20 + rng.NextInt(40)
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO PurchaseOrderItems (POID, ProductID, Quantity, UnitCost) VALUES (@po, @p, @q, @c)",
                        New Dictionary(Of String, Object) From {
                            {"@po", poId}, {"@p", Convert.ToInt32(p("ProductID"))}, {"@q", qty}, {"@c", cost}})
                    total += qty * cost
                Next
                DataAccess.Exec(conn, tx, "UPDATE PurchaseOrders SET TotalAmount = @t WHERE POID = @po",
                    New Dictionary(Of String, Object) From {{"@t", total}, {"@po", poId}})
            Next
        Next
    End Sub

    ''' Removes everything the generator wrote, leaving anything real alone.
    ''' Sample data that can't be taken out again is a trap, not a convenience.
    Public Function Remove() As Integer
        Dim removed = SampleInvoiceCount()

        DataAccess.Execute("DELETE FROM InvoiceItems WHERE InvoiceID IN (SELECT InvoiceID FROM Invoices WHERE IsSample = 1)")
        DataAccess.Execute("DELETE FROM Invoices WHERE IsSample = 1")
        DataAccess.Execute("DELETE FROM PurchaseOrderItems WHERE POID IN (SELECT POID FROM PurchaseOrders WHERE IsSample = 1)")
        DataAccess.Execute("DELETE FROM PurchaseOrders WHERE IsSample = 1")
        DataAccess.Execute("DELETE FROM StockBatches WHERE BatchNumber = 'SAMPLE'")
        DataAccess.Execute("DELETE FROM StockMovements WHERE ReferenceType = 'Production' AND Quantity = 4000")
        Return removed
    End Function

End Module
