Imports System.Data
Imports System.Reflection
Imports System.Windows.Forms
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Free-text search boxes added to the records screens: typing a name, phone
''' number or other keyword narrows the grid to matching rows only.
<TestClass>
Public Class SearchTests

    Private Const NonPublicInstance As BindingFlags = BindingFlags.NonPublic Or BindingFlags.Instance

    Private Shared Function Field(target As Object, name As String) As Object
        Dim f = target.GetType().GetField(name, NonPublicInstance)
        Assert.IsNotNull(f, "no field named " & name)
        Return f.GetValue(target)
    End Function

    Private Shared Function GridOf(uc As Object, fieldName As String) As DataGridView
        Return DirectCast(Field(uc, fieldName), PagedGrid).Grid
    End Function

    Private Shared Function RowCount(grid As DataGridView) As Integer
        Return grid.Rows.Count
    End Function

    Private Shared Function Descendants(c As Control) As List(Of Control)
        Dim all As New List(Of Control)
        For Each child As Control In c.Controls
            all.Add(child)
            all.AddRange(Descendants(child))
        Next
        Return all
    End Function

    Private Shared Function HasText(root As Control, text As String) As Boolean
        Return Descendants(root).Any(Function(c) c.Text IsNot Nothing AndAlso c.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
    End Function

    <TestInitialize>
    Public Sub Setup()
        TestDb.Rebuild()
    End Sub

    ' ===== invoices =====

    <TestMethod>
    Public Sub Invoice_search_matches_by_customer_name()
        Dim alice = TestDb.AddCustomer("Alice Waybill Ltd")
        Dim bob = TestDb.AddCustomer("Bob Distro")
        Dim product = TestDb.AddProduct("SearchTest Feed")
        TestDb.AddInvoice(alice, product, Date.Today)
        TestDb.AddInvoice(bob, product, Date.Today)

        Using host As New Form()
            Dim uc As New ucInvoices(1, True)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            Dim grid = GridOf(uc, "grid")
            Assert.AreEqual(2, RowCount(grid), "both invoices show before searching")

            DirectCast(Field(uc, "txtSearch"), TextBox).Text = "Alice"
            Application.DoEvents()
            Assert.AreEqual(1, RowCount(grid))
            Assert.AreEqual("Alice Waybill Ltd", grid.Rows(0).Cells("Customer").Value.ToString())
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Invoice_search_matches_by_invoice_number()
        Dim cust = TestDb.AddCustomer("Search Customer")
        Dim product = TestDb.AddProduct("SearchTest Feed 2")
        Dim invoiceId = TestDb.AddInvoice(cust, product, Date.Today)
        Dim number = Convert.ToString(TestDb.Scalar("SELECT InvoiceNumber FROM Invoices WHERE InvoiceID=@id",
            TestDb.P("@id", invoiceId)))

        Using host As New Form()
            Dim uc As New ucInvoices(1, True)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            Dim grid = GridOf(uc, "grid")

            DirectCast(Field(uc, "txtSearch"), TextBox).Text = number
            Application.DoEvents()
            Assert.AreEqual(1, RowCount(grid), "searching the exact invoice number finds it")
            host.Close()
        End Using
    End Sub

    ' ===== customers =====

    <TestMethod>
    Public Sub Customer_search_matches_by_phone_number()
        Dim id1 = TestDb.AddCustomer("Phone Match Co")
        TestDb.AddCustomer("No Match Ltd")
        DataAccess.Execute("UPDATE Customers SET Phone='08011112222' WHERE CustomerID=@id",
            New Dictionary(Of String, Object) From {{"@id", id1}})
        DataAccess.Execute("UPDATE Customers SET Phone='08099998888' WHERE CustomerID<>@id",
            New Dictionary(Of String, Object) From {{"@id", id1}})

        Using host As New Form()
            Dim uc As New ucCustomers(1)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            Dim grid = GridOf(uc, "grid")
            Dim before = RowCount(grid)
            Assert.IsTrue(before >= 2, "the two customers just added must show up")

            DirectCast(Field(uc, "txtSearch"), TextBox).Text = "08011112222"
            Application.DoEvents()
            Assert.AreEqual(1, RowCount(grid), "searching a phone number finds only that customer")
            Assert.AreEqual("Phone Match Co", grid.Rows(0).Cells("Name").Value.ToString())
            host.Close()
        End Using
    End Sub

    ' ===== suppliers =====

    <TestMethod>
    Public Sub Supplier_search_matches_by_name()
        DataAccess.Execute("INSERT INTO Suppliers (Name, Category, Phone) VALUES ('Northwind Traders', 'Feed', '0700111222')")
        DataAccess.Execute("INSERT INTO Suppliers (Name, Category, Phone) VALUES ('Other Supplier', 'Feed', '0700333444')")

        Using host As New Form()
            Dim uc As New ucSuppliers(1)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            Dim grid = GridOf(uc, "gridSuppliers")
            Assert.IsTrue(RowCount(grid) >= 2, "both suppliers just added must show up")

            DirectCast(Field(uc, "txtSearchSuppliers"), TextBox).Text = "Northwind"
            Application.DoEvents()
            Assert.AreEqual(1, RowCount(grid))
            Assert.AreEqual("Northwind Traders", grid.Rows(0).Cells("Name").Value.ToString())
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Suppliers_and_purchases_are_separate_full_width_pages()
        Using host As New Form()
            Dim suppliers As New ucSuppliers(1)
            Dim purchases As New ucSuppliers(1, purchaseOrdersOnly:=True)
            suppliers.Dock = DockStyle.Fill
            host.Controls.Add(suppliers)
            host.Show()
            Application.DoEvents()

            Assert.IsTrue(Descendants(suppliers).Contains(GridOf(suppliers, "gridSuppliers")),
                          "the Suppliers tab shows the suppliers table")
            Assert.IsFalse(Descendants(suppliers).Contains(GridOf(suppliers, "gridPurchaseOrders")),
                           "the Suppliers tab must not also squeeze in the purchase orders table")
            Assert.IsTrue(HasText(suppliers, "Suppliers"), "the Suppliers heading is on screen")

            Assert.IsTrue(Descendants(purchases).Contains(GridOf(purchases, "gridPurchaseOrders")),
                          "the Purchases tab shows the purchase orders table")
            Assert.IsFalse(Descendants(purchases).Contains(GridOf(purchases, "gridSuppliers")),
                           "the Purchases tab must not also squeeze in the suppliers table")
            host.Close()
            purchases.Dispose()
        End Using
    End Sub

    <TestMethod>
    Public Sub Purchase_order_search_matches_by_supplier_name()
        Dim supplierId = DataAccess.ExecuteScalarInsert(
            "INSERT INTO Suppliers (Name, Category, Phone) VALUES ('Findable Feeds Ltd', 'Feed', '0700555666')", Nothing)
        Dim otherId = DataAccess.ExecuteScalarInsert(
            "INSERT INTO Suppliers (Name, Category, Phone) VALUES ('Hidden Holdings', 'Feed', '0700777888')", Nothing)
        For Each pair In {(supplierId, "PO-FIND-1"), (otherId, "PO-HIDE-1")}
            DataAccess.Execute(
                "INSERT INTO PurchaseOrders (PONumber, SupplierID, OrderDate, TotalAmount, Status, PaymentStatus, CreatedByUserID) " &
                "VALUES (@n, @s, GETDATE(), 1000, 'Pending', 'Unpaid', (SELECT MIN(UserID) FROM Users))",
                New Dictionary(Of String, Object) From {{"@n", pair.Item2}, {"@s", pair.Item1}})
        Next

        Using host As New Form()
            Dim uc As New ucSuppliers(1, purchaseOrdersOnly:=True)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            Dim grid = GridOf(uc, "gridPurchaseOrders")
            Assert.IsTrue(RowCount(grid) >= 2, "both purchase orders just added must show up")

            DirectCast(Field(uc, "txtSearchPOs"), TextBox).Text = "Findable"
            Application.DoEvents()
            Assert.AreEqual(1, RowCount(grid), "searching a supplier name narrows the orders")
            Assert.AreEqual("PO-FIND-1", grid.Rows(0).Cells("PONumber").Value.ToString())
            host.Close()
        End Using
    End Sub

    ' ===== employees / staff =====

    <TestMethod>
    Public Sub Staff_search_matches_by_name_or_phone()
        TestDb.AddEmployee("Search Staffer")
        TestDb.AddEmployee("Someone Else")

        Using host As New Form()
            Dim uc As New ucEmployees(1)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            Dim grid = GridOf(uc, "gridStaff")
            Assert.IsTrue(RowCount(grid) >= 2, "both employees just added must show up")

            DirectCast(Field(uc, "txtSearchStaff"), TextBox).Text = "Search Staffer"
            Application.DoEvents()
            Assert.AreEqual(1, RowCount(grid))
            host.Close()
        End Using
    End Sub

    ' ===== PagedGrid (used by Finance) =====

    <TestMethod>
    Public Sub PagedGrid_search_narrows_paged_rows_and_export_rows()
        DataAccess.Execute(
            "INSERT INTO Expenses (Category, ExpenseDate, Amount, Note, CreatedByUserID) VALUES ('Rent', GETDATE(), 1000, 'Warehouse rent', (SELECT MIN(UserID) FROM Users))")
        DataAccess.Execute(
            "INSERT INTO Expenses (Category, ExpenseDate, Amount, Note, CreatedByUserID) VALUES ('Utilities', GETDATE(), 500, 'Electricity bill', (SELECT MIN(UserID) FROM Users))")

        Using host As New Form()
            Dim pg As New PagedGrid()
            host.Controls.Add(pg)
            host.Show()
            Application.DoEvents()

            pg.Bind("SELECT ExpenseID, Category, Amount, Note FROM Expenses", "ExpenseID DESC",
                    searchColumns:={"Category", "Note"})
            Application.DoEvents()
            Dim before = pg.Grid.Rows.Count
            Assert.IsTrue(before >= 2, "both expenses just added must show up")
            Assert.AreEqual(before, pg.AllRows().Rows.Count)

            pg.Search("Electricity")
            Application.DoEvents()
            Assert.AreEqual(1, pg.Grid.Rows.Count, "search narrows the visible page")
            Assert.AreEqual(1, pg.AllRows().Rows.Count, "search also narrows the export rows")
            Assert.AreEqual("Utilities", pg.Grid.Rows(0).Cells("Category").Value.ToString())

            pg.Search("")
            Application.DoEvents()
            Assert.AreEqual(before, pg.Grid.Rows.Count, "clearing the search restores every row")
            host.Close()
        End Using
    End Sub

End Class
