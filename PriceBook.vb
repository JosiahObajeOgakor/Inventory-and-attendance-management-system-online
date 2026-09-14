Imports System.Data

''' Candid Purrfect's price book: the distributor / wholesaler / retail price of
''' every product, updated as often as the business needs, with every change
''' kept — what it was, what it became, who changed it, when, and why.
'''
''' The history table exists only in databases of businesses that keep a price
''' book (Company.HasPriceLists). ChewyPets' database never gets it.
Public Module PriceBook

    Public Class PriceUpdate
        Public Property ProductID As Integer
        Public Property Distributor As Decimal
        Public Property Wholesaler As Decimal
        Public Property Retail As Decimal
    End Class

    Public Sub EnsureTables()
        DataAccess.Execute(
            "IF OBJECT_ID('dbo.PriceChanges', 'U') IS NULL " &
            "CREATE TABLE PriceChanges (" &
            "    ChangeID        INT IDENTITY(1,1) PRIMARY KEY," &
            "    ProductID       INT NOT NULL REFERENCES Products(ProductID)," &
            "    OldDistributor  DECIMAL(12,2) NOT NULL," &
            "    OldWholesaler   DECIMAL(12,2) NOT NULL," &
            "    OldRetail       DECIMAL(12,2) NOT NULL," &
            "    NewDistributor  DECIMAL(12,2) NOT NULL," &
            "    NewWholesaler   DECIMAL(12,2) NOT NULL," &
            "    NewRetail       DECIMAL(12,2) NOT NULL," &
            "    ChangedAt       DATETIME2 NOT NULL DEFAULT SYSDATETIME()," &
            "    ChangedByUserID INT NULL REFERENCES Users(UserID)," &
            "    Note            NVARCHAR(200) NULL" &
            ");")
        DataAccess.Execute(
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PriceChanges_Product' AND object_id=OBJECT_ID('dbo.PriceChanges')) " &
            "CREATE INDEX IX_PriceChanges_Product ON PriceChanges(ProductID, ChangedAt);")
    End Sub

    Public Function HasTables() As Boolean
        Return DataAccess.GetTable("SELECT OBJECT_ID('dbo.PriceChanges', 'U')").Rows(0)(0) IsNot DBNull.Value
    End Function

    ''' A price moved by `percent`, rounded to the nearest `roundTo` naira (0 = to the kobo).
    Public Function Adjusted(price As Decimal, percent As Decimal, roundTo As Decimal) As Decimal
        Dim v = price * (1D + percent / 100D)
        If roundTo > 0 Then v = Math.Round(v / roundTo, MidpointRounding.AwayFromZero) * roundTo
        Return Math.Max(0D, Math.Round(v, 2))
    End Function

    ''' Saves new prices in one go and records each real change. Lines whose
    ''' prices didn't move are skipped, so re-saving a list writes no noise into
    ''' the history. All or nothing: returns how many products changed.
    Public Function Apply(updates As IEnumerable(Of PriceUpdate), userId As Integer, note As String) As Integer
        Dim lines = updates.ToList()
        If lines.Any(Function(u) u.Distributor < 0 OrElse u.Wholesaler < 0 OrElse u.Retail < 0) Then
            Throw New ArgumentException("A price can't be negative.")
        End If
        EnsureTables()
        Dim noteValue As Object = If(String.IsNullOrWhiteSpace(note), CObj(DBNull.Value), note.Trim())
        Dim byUser As Object = If(userId > 0, CObj(userId), DBNull.Value)

        Return DataAccess.InTransaction(
            Function(conn, tx)
                Dim changed = 0
                For Each u In lines
                    Dim current = DataAccess.TableIn(conn, tx,
                        "SELECT PriceDistributor, PriceWholesaler, PriceRetail FROM Products WITH (UPDLOCK) WHERE ProductID = @p",
                        New Dictionary(Of String, Object) From {{"@p", u.ProductID}})
                    If current.Rows.Count = 0 Then Continue For
                    Dim oldD = Convert.ToDecimal(current.Rows(0)(0))
                    Dim oldW = Convert.ToDecimal(current.Rows(0)(1))
                    Dim oldR = Convert.ToDecimal(current.Rows(0)(2))
                    If oldD = u.Distributor AndAlso oldW = u.Wholesaler AndAlso oldR = u.Retail Then Continue For

                    DataAccess.Exec(conn, tx,
                        "UPDATE Products SET PriceDistributor = @d, PriceWholesaler = @w, PriceRetail = @r, SellingPrice = @r WHERE ProductID = @p",
                        New Dictionary(Of String, Object) From {{"@d", u.Distributor}, {"@w", u.Wholesaler}, {"@r", u.Retail}, {"@p", u.ProductID}})
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO PriceChanges (ProductID, OldDistributor, OldWholesaler, OldRetail, NewDistributor, NewWholesaler, NewRetail, ChangedByUserID, Note) " &
                        "VALUES (@p, @od, @ow, @or, @d, @w, @r, @u, @n)",
                        New Dictionary(Of String, Object) From {
                            {"@p", u.ProductID}, {"@od", oldD}, {"@ow", oldW}, {"@or", oldR},
                            {"@d", u.Distributor}, {"@w", u.Wholesaler}, {"@r", u.Retail}, {"@u", byUser}, {"@n", noteValue}})
                    changed += 1
                Next
                Return changed
            End Function)
    End Function

    Public Class NewProduct
        Public Property Name As String
        Public Property CategoryID As Integer
        Public Property Unit As String = "Bag"
        Public Property Distributor As Decimal
        Public Property Wholesaler As Decimal
        Public Property Retail As Decimal
    End Class

    ''' Adds products straight onto the price list — a long list typed in one
    ''' sitting. Each gets its own product code ("CP-00012"), its own in-store
    ''' barcode, and a first line in the price history. No stock is booked in:
    ''' that happens when goods are produced or received. All or nothing.
    ''' Returns the new product IDs.
    Public Function AddProducts(products As IEnumerable(Of NewProduct), userId As Integer, note As String) As List(Of Integer)
        Dim lines = products.Where(Function(p) Not String.IsNullOrWhiteSpace(p.Name)).ToList()
        If lines.Any(Function(p) p.Distributor < 0 OrElse p.Wholesaler < 0 OrElse p.Retail < 0) Then
            Throw New ArgumentException("A price can't be negative.")
        End If
        Dim duplicate = lines.GroupBy(Function(p) p.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(Function(g) g.Count() > 1)
        If duplicate IsNot Nothing Then Throw New ArgumentException($"""{duplicate.Key}"" is on the list twice.")
        EnsureTables()
        Dim noteValue As Object = If(String.IsNullOrWhiteSpace(note), CObj("Added to price list"), note.Trim())
        Dim byUser As Object = If(userId > 0, CObj(userId), DBNull.Value)
        Dim codePrefix = New String(Company.Current.DocumentPrefix.Where(AddressOf Char.IsUpper).ToArray())
        If codePrefix = "" Then codePrefix = "P"

        Return DataAccess.InTransaction(
            Function(conn, tx)
                Dim ids As New List(Of Integer)
                For Each p In lines
                    Dim exists = DataAccess.ScalarIn(conn, tx, "SELECT COUNT(*) FROM Products WHERE Name = @n AND IsActive = 1",
                                                     New Dictionary(Of String, Object) From {{"@n", p.Name.Trim()}})
                    If Convert.ToInt32(exists) > 0 Then Throw New ArgumentException($"""{p.Name.Trim()}"" is already on the price list.")

                    Dim id = DataAccess.InsertReturningId(conn, tx,
                        "INSERT INTO Products (SKU, Name, CategoryID, Unit, ReorderLevel, CostPrice, SellingPrice, PriceRetail, PriceWholesaler, PriceDistributor) " &
                        "VALUES (@tmp, @n, @c, @u, 0, 0, @r, @r, @w, @d)",
                        New Dictionary(Of String, Object) From {
                            {"@tmp", "NEW-" & Guid.NewGuid().ToString("N").Substring(0, 12)}, {"@n", p.Name.Trim()},
                            {"@c", p.CategoryID}, {"@u", If(String.IsNullOrWhiteSpace(p.Unit), "Bag", p.Unit.Trim())},
                            {"@r", p.Retail}, {"@w", p.Wholesaler}, {"@d", p.Distributor}})
                    DataAccess.Exec(conn, tx, "UPDATE Products SET SKU = @s, Barcode = @b WHERE ProductID = @id",
                        New Dictionary(Of String, Object) From {
                            {"@s", $"{codePrefix}-{id:D5}"}, {"@b", Barcodes.MintInternalBarcode(id)}, {"@id", id}})
                    DataAccess.Exec(conn, tx,
                        "INSERT INTO PriceChanges (ProductID, OldDistributor, OldWholesaler, OldRetail, NewDistributor, NewWholesaler, NewRetail, ChangedByUserID, Note) " &
                        "VALUES (@p, 0, 0, 0, @d, @w, @r, @u, @n)",
                        New Dictionary(Of String, Object) From {
                            {"@p", id}, {"@d", p.Distributor}, {"@w", p.Wholesaler}, {"@r", p.Retail}, {"@u", byUser}, {"@n", noteValue}})
                    ids.Add(id)
                Next
                Return ids
            End Function)
    End Function

    ''' When prices last changed, or Nothing if they never have.
    Public Function LastUpdated() As Date?
        If Not HasTables() Then Return Nothing
        Dim v = DataAccess.GetTable("SELECT MAX(ChangedAt) FROM PriceChanges").Rows(0)(0)
        Return If(v Is DBNull.Value, CType(Nothing, Date?), Convert.ToDateTime(v))
    End Function

    ''' The price book as it stands, for the paged screen.
    Public Const CurrentPricesSql As String =
        "SELECT p.ProductID, c.Name AS Category, p.Name AS Product, p.SKU, p.Unit, " &
        "p.PriceDistributor AS Distributor, p.PriceWholesaler AS Wholesaler, p.PriceRetail AS Retail, " &
        "ISNULL((SELECT SUM(sb.QuantityOnHand) FROM StockBatches sb WHERE sb.ProductID = p.ProductID), 0) AS InStock, " &
        "(SELECT MAX(pc.ChangedAt) FROM PriceChanges pc WHERE pc.ProductID = p.ProductID) AS LastChanged " &
        "FROM Products p JOIN Categories c ON c.CategoryID = p.CategoryID WHERE p.IsActive = 1"

    ''' Every change, newest first.
    Public Const HistorySql As String =
        "SELECT pc.ChangeID, pc.ChangedAt, p.Name AS Product, " &
        "pc.OldDistributor AS DistributorWas, pc.NewDistributor AS DistributorNow, " &
        "pc.OldWholesaler AS WholesalerWas, pc.NewWholesaler AS WholesalerNow, " &
        "pc.OldRetail AS RetailWas, pc.NewRetail AS RetailNow, " &
        "ISNULL(u.FullName, '') AS ChangedBy, ISNULL(pc.Note, '') AS Note " &
        "FROM PriceChanges pc JOIN Products p ON p.ProductID = pc.ProductID LEFT JOIN Users u ON u.UserID = pc.ChangedByUserID"

End Module
