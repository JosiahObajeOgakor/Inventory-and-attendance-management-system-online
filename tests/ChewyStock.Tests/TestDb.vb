Imports System.Data
Imports System.Diagnostics
Imports System.IO
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Shared test database: its own LocalDB instance ("ChewyStockTest") and its own
''' data folder, built from the real Schema.sql through the app's own first-run
''' code. Nothing here can reach a live ChewyStock database.
<TestClass>
Public Class TestDb

    Public Const Instance As String = "ChewyStockTest"

    <AssemblyInitialize>
    Public Shared Sub AssemblyInit(context As TestContext)
        LocalDb("create", Instance)
        LocalDb("start", Instance)
        DropDatabase()
        Dim err = DbBootstrap.EnsureDatabase()
        Assert.AreEqual("", err, "Test database could not be created")
    End Sub

    <AssemblyCleanup>
    Public Shared Sub AssemblyDone()
        DropDatabase()
        LocalDb("stop", Instance)
        LocalDb("delete", Instance)
        Try
            Directory.Delete(DbBootstrap.DataDirectory(), True)
        Catch
        End Try
    End Sub

    ''' Drops and rebuilds the schema — for tests that need a known-empty start.
    Public Shared Sub Rebuild()
        DropDatabase()
        Assert.AreEqual("", DbBootstrap.EnsureDatabase())
    End Sub

    Private Shared Sub LocalDb(verb As String, instance As String)
        Try
            Using p = Process.Start(New ProcessStartInfo("SqlLocalDB.exe", $"{verb} ""{instance}""") With {
                .UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardOutput = True, .RedirectStandardError = True})
                p.WaitForExit(30000)
            End Using
        Catch
        End Try
    End Sub

    Private Shared Sub DropDatabase()
        Try
            Dim csb As New SqlClient.SqlConnectionStringBuilder(ConnectionString()) With {.InitialCatalog = "master"}
            Using conn As New SqlClient.SqlConnection(csb.ConnectionString)
                conn.Open()
                Using cmd As New SqlClient.SqlCommand(
                    "IF DB_ID('StockDeskDB') IS NOT NULL BEGIN ALTER DATABASE StockDeskDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE StockDeskDB; END", conn)
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        Catch
        End Try
        ' Leftover files would be re-attached by the bootstrap; clear them too.
        Try
            For Each f In Directory.GetFiles(DbBootstrap.DataDirectory(), "StockDeskDB*")
                File.Delete(f)
            Next
        Catch
        End Try
    End Sub

    Public Shared Function ConnectionString() As String
        Return Configuration.ConfigurationManager.ConnectionStrings("StockDeskDB").ConnectionString
    End Function

    ' ===== small query helpers =====

    Public Shared Function Scalar(sql As String, Optional params As Dictionary(Of String, Object) = Nothing) As Object
        Return DataAccess.GetTable(sql, params).Rows(0)(0)
    End Function

    Public Shared Function Count(sql As String, Optional params As Dictionary(Of String, Object) = Nothing) As Integer
        Return Convert.ToInt32(Scalar(sql, params))
    End Function

    Public Shared Function Table(sql As String, Optional params As Dictionary(Of String, Object) = Nothing) As DataTable
        Return DataAccess.GetTable(sql, params)
    End Function

    Public Shared Sub Exec(sql As String, Optional params As Dictionary(Of String, Object) = Nothing)
        DataAccess.Execute(sql, params)
    End Sub

    Public Shared Function P(ParamArray pairs As Object()) As Dictionary(Of String, Object)
        Dim d As New Dictionary(Of String, Object)
        For i = 0 To pairs.Length - 2 Step 2
            d(Convert.ToString(pairs(i))) = pairs(i + 1)
        Next
        Return d
    End Function

    ' ===== fixtures =====

    Public Shared Function AddProduct(name As String, Optional cost As Decimal = 1000D) As Integer
        Return DataAccess.ExecuteScalarInsert(
            "INSERT INTO Products (SKU, Name, CategoryID, Unit, ReorderLevel, CostPrice, SellingPrice, PriceRetail, PriceWholesaler, PriceDistributor) " &
            "VALUES (@sku, @n, (SELECT MIN(CategoryID) FROM Categories), 'Bag', 5, @c, @c, @c, @c, @c)",
            P("@sku", "T-" & Guid.NewGuid().ToString("N").Substring(0, 8), "@n", name, "@c", cost))
    End Function

    Public Shared Function AddCustomer(name As String) As Integer
        Return DataAccess.ExecuteScalarInsert(
            "INSERT INTO Customers (Name, CustomerType, RebateRatePct, CreditLimit, Balance) VALUES (@n, 'Retailer', 1.0, 100000, 0)",
            P("@n", name))
    End Function

    Public Shared Function AddEmployee(name As String, Optional monthlySalary As Decimal = 0D) As Integer
        Return DataAccess.ExecuteScalarInsert(
            "INSERT INTO Employees (FullName, Position, Phone, MonthlySalary) VALUES (@n, 'Clerk', '0800', @s)",
            P("@n", name, "@s", monthlySalary))
    End Function

    ''' An invoice with one line, dated `on`, fully paid unless told otherwise.
    Public Shared Function AddInvoice(customerId As Integer, productId As Integer, [on] As Date,
                                      Optional total As Decimal = 10000D, Optional paid As Decimal? = Nothing,
                                      Optional status As String = "Paid") As Integer
        Dim amountPaid = If(paid.HasValue, paid.Value, total)
        Dim id = DataAccess.ExecuteScalarInsert(
            "INSERT INTO Invoices (InvoiceNumber, CustomerID, InvoiceDate, Subtotal, TotalAmount, AmountPaid, VATRate, VATAmount, [Status], CreatedByUserID) " &
            "VALUES (@num, @c, @d, @t, @t, @p, 0, 0, @s, (SELECT MIN(UserID) FROM Users))",
            P("@num", "INV-T-" & Guid.NewGuid().ToString("N").Substring(0, 10), "@c", customerId, "@d", [on],
              "@t", total, "@p", amountPaid, "@s", status))
        DataAccess.Execute(
            "INSERT INTO InvoiceItems (InvoiceID, ProductID, Quantity, UnitPrice, UnitCost, LineTotal) VALUES (@i, @p, 1, @t, 0, @t)",
            P("@i", id, "@p", productId, "@t", total))
        Return id
    End Function

End Class
