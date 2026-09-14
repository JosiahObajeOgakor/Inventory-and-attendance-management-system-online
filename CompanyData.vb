Imports System.Data.SqlClient

''' Getting a second business's database ready. The home (ChewyPets) database
''' is never touched here.
'''
''' Every new database is laid out from Schema.sql, and Schema.sql also carries
''' ChewyPets' starter records — its products, customers, suppliers, staff,
''' expenses and warehouses. None of that belongs to Candid Purrfect, so it is
''' taken out once, the first time the business is opened.
Public Module CompanyData

    Private Const StarterClearedKey As String = "company.starterDataCleared"

    ''' Returns "" when ready, else the error text for the sign-in screen.
    Public Function Prepare(company As Company) As String
        If company.IsHome Then Return ""
        Try
            ClearStarterData()
            If company.HasPriceLists Then PriceBook.EnsureTables()
            Return ""
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

    ''' Removes the ChewyPets starter rows, one at a time. A row something real
    ''' already points at (a sale made on a starter product, say) is kept rather
    ''' than taking the sale with it. Runs once per database: afterwards a Candid
    ''' customer who happens to share a name with a starter row is left alone.
    Friend Sub ClearStarterData()
        If DataAccess.GetTable("SELECT 1 FROM AppSettings WHERE SettingKey = @k",
                               New Dictionary(Of String, Object) From {{"@k", StarterClearedKey}}).Rows.Count > 0 Then Return

        ' Children before parents, so as much as possible can go.
        RemoveEach("Expenses", "ExpenseID", "Note IN ('Warehouse site','2 staff','Power & water','Delivery fuel')")
        RemoveEach("StockBatches", "BatchID", "BatchNumber IN ('B24-118','B24-097','B24-102','B24-088','B24-121')")
        RemoveEach("Products", "ProductID", "SKU IN ('SKU-1001','SKU-1002','SKU-1003','SKU-1004','SKU-1005')")
        RemoveEach("Customers", "CustomerID", "TaxID IN ('TIN-5561200','TIN-6672311','TIN-7783422')")
        RemoveEach("Suppliers", "SupplierID", "TaxID IN ('TIN-1004552','TIN-2288140','TIN-3390871')")
        RemoveEach("Employees", "EmployeeID", "Phone IN ('0803 000 0001','0803 000 0002')")
        ' Unused starter logins only; real accounts arrive by mirroring at sign-in.
        RemoveEach("Users", "UserID", "PasswordHash = 'SETUP_REQUIRED' AND Username IN ('ifeoma.c','david.o')")
        RemoveEach("Warehouses", "WarehouseID", "Name IN ('Lawal warehouse','Shore warehouse')")

        ' A table emptied of starter rows numbers its own records from 1 again.
        For Each table In {"Expenses", "StockBatches", "Products", "Suppliers", "Employees", "Warehouses"}
            DataAccess.Execute($"IF NOT EXISTS (SELECT 1 FROM {table}) DBCC CHECKIDENT ('{table}', RESEED, 0);")
        Next

        ' Stock has to sit somewhere.
        DataAccess.Execute("IF NOT EXISTS (SELECT 1 FROM Warehouses) INSERT INTO Warehouses (Name, Location) VALUES ('Main store', NULL);")
        ' What Candid Purrfect sells beyond food.
        For Each category In {"Accessories", "Care"}
            DataAccess.Execute("IF NOT EXISTS (SELECT 1 FROM Categories WHERE Name = @n) INSERT INTO Categories (Name) VALUES (@n);",
                               New Dictionary(Of String, Object) From {{"@n", category}})
        Next

        DataAccess.Execute("INSERT INTO AppSettings (SettingKey, SettingValue) VALUES (@k, @v);",
                           New Dictionary(Of String, Object) From {{"@k", StarterClearedKey}, {"@v", DateTime.Now.ToString("s")}})
    End Sub

    Private Sub RemoveEach(table As String, idColumn As String, starterRows As String)
        Dim ids = DataAccess.GetTable($"SELECT {idColumn} FROM {table} WHERE {starterRows}")
        For Each r As DataRow In ids.Rows
            Try
                DataAccess.Execute($"DELETE FROM {table} WHERE {idColumn} = @id",
                                   New Dictionary(Of String, Object) From {{"@id", r(0)}})
            Catch ex As SqlException When ex.Number = 547
                ' In use by a real record — keep it.
            End Try
        Next
    End Sub

End Module
