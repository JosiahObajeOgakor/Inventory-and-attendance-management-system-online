Imports System.Data
Imports System.Drawing
Imports System.Windows.Forms
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' The two businesses behind the sign-in screen. What matters: each prints its
''' own name, banks and stamp; their records never mix; and one account signs
''' into both with the same password.
<TestClass>
Public Class CompanyTests

    <TestInitialize>
    Public Sub StartAtHome()
        Company.Use(Company.Home)
    End Sub

    ''' Whatever a test does, the next one (in any class) must see ChewyPets.
    <TestCleanup>
    Public Sub BackHome()
        Company.Use(Company.Home)
    End Sub

    Private Shared Function SignIn(username As String, password As String, company As Company) As Auth.LoginResult
        Company.Use(Company.Home)
        Dim r = Auth.TryLogin(username, password)
        Assert.AreEqual(Auth.LoginOutcome.Success, r.Outcome, r.Message)
        Assert.AreEqual("", Auth.EnterCompany(r, company))
        Return r
    End Function

    Private Shared Function NewAccount(role As String) As String
        Dim username = "dual." & Guid.NewGuid().ToString("N").Substring(0, 8)
        Company.Use(Company.Home)
        Auth.CreateAccount("Dual " & role, username, "Passw0rd!2026", role)
        Return username
    End Function

    ' ===== identity on documents =====

    <TestMethod>
    Public Sub Candid_receipts_carry_candid_bank_accounts()
        Company.Use(Company.CandidPurrfect)
        Dim banks = AppInfo.BankAccounts()

        Assert.AreEqual(2, banks.Count)
        Assert.AreEqual("Sterling Bank PLC", banks(0).Bank)
        Assert.AreEqual("0097166161", banks(0).AccountNumber)
        Assert.AreEqual("First Bank PLC", banks(1).Bank)
        Assert.AreEqual("2045958847", banks(1).AccountNumber)
        Assert.IsTrue(banks.All(Function(b) b.AccountName = "Candid Purrfect Pets Company Ltd"))
        Assert.IsFalse(banks.Any(Function(b) b.AccountNumber = "8676752988" OrElse b.AccountNumber = "2047950632"),
                       "a ChewyPets account on a Candid receipt would send the money to the wrong company")
    End Sub

    <TestMethod>
    Public Sub Each_business_prints_its_own_name()
        Company.Use(Company.CandidPurrfect)
        Assert.AreEqual("Candid Purrfect Pets Company Ltd", AppInfo.CompanyName)
        Assert.AreEqual("", AppInfo.CompanyAddress, "Candid must never borrow the ChewyPets address")

        Company.Use(Company.ChewyPets)
        Assert.AreEqual("ChewyPetsFeed", AppInfo.CompanyName)
    End Sub

    <TestMethod>
    Public Sub Each_business_has_its_own_stamp_trimmed_to_the_artwork()
        Dim chewy = Company.ChewyPets.Signature
        Dim candid = Company.CandidPurrfect.Signature
        Assert.IsNotNull(chewy, "the new ChewyPets stamp must load")
        Assert.IsNotNull(candid, "the Candid Purrfect stamp must load")
        Assert.AreNotSame(chewy, candid)

        ' The uploads are 1280 wide with the round stamp in the middle; the white
        ' surround is cut so the stamp fills the space it's printed in.
        Using original = Image.FromFile(AppPaths.Asset("candidPurffect.jpeg"))
            Assert.IsTrue(candid.Width < original.Width * 0.8, $"stamp not trimmed: {candid.Width} of {original.Width}")
        End Using

        Company.Use(Company.CandidPurrfect)
        Assert.AreSame(candid, Theme.Signature, "receipts take the stamp of the business signed into")
        Company.Use(Company.ChewyPets)
        Assert.AreSame(chewy, Theme.Signature)
    End Sub

    <TestMethod>
    Public Sub A_blank_border_is_trimmed_and_a_full_image_is_left_alone()
        Using canvas As New Bitmap(200, 100)
            Using g = Graphics.FromImage(canvas)
                g.Clear(Color.White)
                g.FillRectangle(Brushes.Blue, 80, 30, 40, 40)
            End Using
            Dim trimmed = Company.TrimMargins(New Bitmap(canvas))
            Assert.IsTrue(trimmed.Width < 70 AndAlso trimmed.Height < 70, $"{trimmed.Width}x{trimmed.Height}")
        End Using

        Dim solid As New Bitmap(50, 50)
        Using g = Graphics.FromImage(solid)
            g.Clear(Color.Navy)
        End Using
        Assert.AreEqual(50, Company.TrimMargins(solid).Width)
    End Sub

    ' ===== separate books =====

    <TestMethod>
    Public Sub Candid_keeps_its_own_database_on_the_same_server()
        Dim home = New SqlClient.SqlConnectionStringBuilder(Company.ChewyPets.ConnectionString)
        Dim candid = New SqlClient.SqlConnectionStringBuilder(Company.CandidPurrfect.ConnectionString)
        Assert.AreEqual(home.DataSource, candid.DataSource)
        Assert.AreEqual("CandidPurrfectDB", candid.InitialCatalog)
        Assert.AreNotEqual(home.InitialCatalog, candid.InitialCatalog)
    End Sub

    <TestMethod>
    Public Sub Signing_into_candid_opens_candids_books_and_its_records_stay_there()
        Dim user = NewAccount("Warehouse Clerk")
        SignIn(user, "Passw0rd!2026", Company.CandidPurrfect)

        Assert.AreSame(Company.CandidPurrfect, Company.Current)
        Assert.AreEqual("CandidPurrfectDB", Convert.ToString(TestDb.Scalar("SELECT DB_NAME()")))
        Dim marker = "Candid-only customer " & Guid.NewGuid().ToString("N").Substring(0, 6)
        TestDb.AddCustomer(marker)
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Customers WHERE Name = @n", TestDb.P("@n", marker)))

        SignIn(user, "Passw0rd!2026", Company.ChewyPets)
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Customers WHERE Name = @n", TestDb.P("@n", marker)),
                        "a Candid customer must never show up in ChewyPets")
    End Sub

    ' ===== one account, both businesses =====

    <TestMethod>
    Public Sub One_account_signs_into_both_businesses()
        For Each role In {"Admin", "Warehouse Clerk"}
            Dim user = NewAccount(role)

            Dim candid = SignIn(user, "Passw0rd!2026", Company.CandidPurrfect)
            Dim mirrored = TestDb.Table("SELECT u.FullName, r.RoleName FROM Users u JOIN Roles r ON r.RoleID = u.RoleID WHERE u.UserID = @id",
                                        TestDb.P("@id", candid.UserID))
            Assert.AreEqual(1, mirrored.Rows.Count, "the id handed to the app must be a user in Candid's own database")
            Assert.AreEqual(role, Convert.ToString(mirrored.Rows(0)("RoleName")))

            Dim chewy = SignIn(user, "Passw0rd!2026", Company.ChewyPets)
            Assert.AreEqual(user, Convert.ToString(TestDb.Scalar("SELECT Username FROM Users WHERE UserID = @id", TestDb.P("@id", chewy.UserID))))
        Next
    End Sub

    <TestMethod>
    Public Sub Signing_in_twice_does_not_duplicate_the_account()
        Dim user = NewAccount("Warehouse Clerk")
        Dim first = SignIn(user, "Passw0rd!2026", Company.CandidPurrfect)
        Dim second = SignIn(user, "Passw0rd!2026", Company.CandidPurrfect)
        Assert.AreEqual(first.UserID, second.UserID)
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Users WHERE Username = @u", TestDb.P("@u", user)))
    End Sub

    <TestMethod>
    Public Sub A_password_changed_while_in_candid_works_for_chewypets_too()
        Dim user = NewAccount("Warehouse Clerk")
        Dim r = SignIn(user, "Passw0rd!2026", Company.CandidPurrfect)

        Auth.SetPassword(r.UserID, "Changed!2027x")

        Company.Use(Company.Home)
        Assert.AreEqual(Auth.LoginOutcome.BadPassword, Auth.TryLogin(user, "Passw0rd!2026").Outcome)
        SignIn(user, "Changed!2027x", Company.ChewyPets)
    End Sub

    <TestMethod>
    Public Sub A_disabled_account_is_kept_out_of_both_businesses()
        Dim user = NewAccount("Warehouse Clerk")
        SignIn(user, "Passw0rd!2026", Company.CandidPurrfect)
        Auth.ToggleActive(user)

        Company.Use(Company.Home)
        Assert.AreEqual(Auth.LoginOutcome.Disabled, Auth.TryLogin(user, "Passw0rd!2026").Outcome)
    End Sub

    <TestMethod>
    Public Sub Check_in_inside_candid_is_recorded_in_candids_attendance()
        Dim user = NewAccount("Warehouse Clerk")
        Dim r = SignIn(user, "Passw0rd!2026", Company.CandidPurrfect)
        Dim at = Attendance.CheckIn(r.UserID, r.FullName)

        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM AttendanceEvents WHERE UserID = @u AND EventType = 'In'", TestDb.P("@u", r.UserID)))
        Assert.IsTrue(Math.Abs((DateTime.Now - at).TotalMinutes) < 1)
    End Sub

    ' ===== each business is its own app =====

    <TestMethod>
    Public Sub The_app_takes_the_name_and_logo_of_the_business_signed_into()
        Company.Use(Company.ChewyPets)
        Assert.AreEqual("ChewyStock", Theme.AppName, "ChewyPets keeps its name")
        Assert.IsNotNull(Theme.Logo, "the ChewyPet logo must load")
        Dim chewyLogo = Theme.Logo

        Company.Use(Company.CandidPurrfect)
        Assert.AreEqual("Candid Purrfect", Theme.AppName)
        Assert.IsNotNull(Theme.Logo, "the Candid Purrfect logo must load")
        Assert.AreNotSame(chewyLogo, Theme.Logo)
    End Sub

    <TestMethod>
    Public Sub The_chewypet_logo_loses_its_frame_and_white_margin()
        Using original = Image.FromFile(AppPaths.Asset("chewypetfeedslogo.jpeg"))
            Assert.IsTrue(Company.ChewyPets.Logo.Width < original.Width * 0.9,
                          $"logo not trimmed: {Company.ChewyPets.Logo.Width} of {original.Width}")
        End Using
    End Sub

    <TestMethod>
    Public Sub Receipt_numbers_carry_the_business_name()
        Company.Use(Company.ChewyPets)
        Assert.IsTrue(Numbering.NextNumber(Nothing, Nothing, "INV", "Invoices", "InvoiceNumber", Date.Today).StartsWith("ChewyStock-"))
        Company.Use(Company.CandidPurrfect)
        Assert.IsTrue(Numbering.NextNumber(Nothing, Nothing, "INV", "Invoices", "InvoiceNumber", Date.Today).StartsWith("CandidPurrfect-"))
    End Sub

    <TestMethod>
    Public Sub The_welcome_clip_is_chewypets_only()
        Company.Use(Company.ChewyPets)
        Assert.IsTrue(IO.File.Exists(frmClerkWelcome.VideoPath()))
        Company.Use(Company.CandidPurrfect)
        Assert.AreEqual("", frmClerkWelcome.VideoPath())
    End Sub

    <TestMethod>
    Public Sub A_new_candid_database_starts_with_nothing_from_chewypets_and_numbers_from_one()
        Company.Use(Company.Home)
        TestDb.DropDatabase(Company.CandidPurrfect.DatabaseName)
        SignIn(NewAccount("Admin"), "Passw0rd!2026", Company.CandidPurrfect)

        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Products"), "no ChewyPets products")
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Suppliers"), "no ChewyPets suppliers")
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Employees"), "no ChewyPets staff")
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Expenses"), "no ChewyPets expenses")
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Customers WHERE Name <> 'Walk-in Customer'"), "no ChewyPets customers")
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Warehouses WHERE Name IN ('Lawal warehouse','Shore warehouse')"))
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Warehouses"), "one store for stock to sit in")
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Users WHERE PasswordHash = 'SETUP_REQUIRED'"))
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Categories WHERE Name = 'Accessories'"))

        Assert.AreEqual(1, TestDb.AddProduct("First Candid product"), "Candid numbers its own records from 1")
    End Sub

    <TestMethod>
    Public Sub Clearing_the_starter_data_runs_once_and_never_touches_chewypets()
        SignIn(NewAccount("Admin"), "Passw0rd!2026", Company.CandidPurrfect)
        TestDb.Exec("INSERT INTO Customers (Name, CustomerType, TaxID) VALUES ('PetMart Lagos', 'Distributor', 'TIN-5561200')")
        SignIn(NewAccount("Admin"), "Passw0rd!2026", Company.CandidPurrfect)
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM Customers WHERE TaxID = 'TIN-5561200'"),
                        "a real Candid customer added later is not starter data")

        Company.Use(Company.Home)
        Assert.IsTrue(TestDb.Scalar("SELECT OBJECT_ID('dbo.PriceChanges', 'U')") Is DBNull.Value,
                      "ChewyPets' database gets no price book")
    End Sub

    ' ===== candid buys, it doesn't produce =====

    <TestMethod>
    Public Sub A_candid_purchase_goes_straight_into_stock()
        Dim r = SignIn(NewAccount("Warehouse Clerk"), "Passw0rd!2026", Company.CandidPurrfect)
        Dim tag = Guid.NewGuid().ToString("N").Substring(0, 5)
        Dim supplier = DataAccess.ExecuteScalarInsert("INSERT INTO Suppliers (Name) VALUES (@n)", TestDb.P("@n", "Pet Wholesale " & tag))
        Dim product = TestDb.AddProduct("Bought Kibble " & tag, 0D)
        TestDb.Exec("UPDATE Products SET Barcode = NULL WHERE ProductID = @p", TestDb.P("@p", product))
        Dim req As New Purchasing.PurchaseRequest() With {
            .SupplierID = supplier, .SupplierName = "Pet Wholesale " & tag, .OrderDate = Date.Today, .ReceiveNow = True}
        req.Lines.Add(New Purchasing.PurchaseLine() With {.ProductID = product, .ProductName = "Bought Kibble", .Quantity = 40, .UnitCost = 2500D})

        Dim saved = Purchasing.Save(req, r.UserID)

        Assert.AreEqual(40, TestDb.Count("SELECT ISNULL(SUM(QuantityOnHand),0) FROM StockBatches WHERE ProductID = @p", TestDb.P("@p", product)),
                        "the goods are on the shelf the moment the purchase is saved")
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM StockBatches WHERE ProductID = @p AND BatchNumber = @b", TestDb.P("@p", product, "@b", saved.PONumber)),
                        "the batch traces back to the purchase")
        Assert.AreEqual(1, TestDb.Count("SELECT COUNT(*) FROM StockMovements WHERE ProductID = @p AND MovementType = 'IN' AND ReferenceType = 'PurchaseOrder' AND ReferenceID = @po",
                                        TestDb.P("@p", product, "@po", saved.POID)), "and shows in the stock history")
        Assert.AreEqual("Received", Convert.ToString(TestDb.Scalar("SELECT Status FROM PurchaseOrders WHERE POID = @po", TestDb.P("@po", saved.POID))))
        Assert.AreEqual(2500D, Convert.ToDecimal(TestDb.Scalar("SELECT CostPrice FROM Products WHERE ProductID = @p", TestDb.P("@p", product))),
                        "cost becomes what was paid, so profit on the next sale is right")
        Assert.AreEqual(Barcodes.MintInternalBarcode(product), Convert.ToString(TestDb.Scalar("SELECT Barcode FROM Products WHERE ProductID = @p", TestDb.P("@p", product))))
        Assert.IsTrue(saved.PONumber.StartsWith("CandidPurrfect-"))

        ' A second purchase tops the same product up rather than replacing it.
        Dim again As New Purchasing.PurchaseRequest() With {
            .SupplierID = supplier, .SupplierName = "Pet Wholesale " & tag, .OrderDate = Date.Today, .ReceiveNow = True}
        again.Lines.Add(New Purchasing.PurchaseLine() With {.ProductID = product, .ProductName = "Bought Kibble", .Quantity = 10, .UnitCost = 2600D})
        Purchasing.Save(again, r.UserID)
        Assert.AreEqual(50, TestDb.Count("SELECT ISNULL(SUM(QuantityOnHand),0) FROM StockBatches WHERE ProductID = @p", TestDb.P("@p", product)))
    End Sub

    <TestMethod>
    Public Sub A_chewypets_purchase_order_still_waits_to_be_received()
        Company.Use(Company.ChewyPets)
        Dim tag = Guid.NewGuid().ToString("N").Substring(0, 5)
        Dim supplier = DataAccess.ExecuteScalarInsert("INSERT INTO Suppliers (Name) VALUES (@n)", TestDb.P("@n", "Grain Co " & tag))
        Dim product = TestDb.AddProduct("Ordered Maize " & tag)
        Dim user = TestDb.Count("SELECT MIN(UserID) FROM Users")
        Dim req As New Purchasing.PurchaseRequest() With {.SupplierID = supplier, .SupplierName = "Grain Co " & tag}
        req.Lines.Add(New Purchasing.PurchaseLine() With {.ProductID = product, .ProductName = "Maize", .Quantity = 40, .UnitCost = 900D})

        Dim saved = Purchasing.Save(req, user)

        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM StockBatches WHERE ProductID = @p", TestDb.P("@p", product)),
                        "ChewyPets orders now and receives later, exactly as before")
        Assert.AreEqual("Pending", Convert.ToString(TestDb.Scalar("SELECT Status FROM PurchaseOrders WHERE POID = @po", TestDb.P("@po", saved.POID))))
    End Sub

    <TestMethod>
    Public Sub Candid_inventory_records_purchases_and_chewypets_keeps_production()
        Company.Use(Company.ChewyPets)
        Using screen As New ucInventory(1, isAdminUser:=False)
            Assert.IsTrue(HasButton(screen, "+ Record production"))
            Assert.IsFalse(HasButton(screen, "+ Record purchase"))
        End Using

        Dim r = SignIn(NewAccount("Warehouse Clerk"), "Passw0rd!2026", Company.CandidPurrfect)
        Using screen As New ucInventory(r.UserID, isAdminUser:=False)
            Assert.IsTrue(HasButton(screen, "+ Record purchase"), "Candid buys what it sells")
            Assert.IsFalse(HasButton(screen, "+ Record production"), "Candid doesn't produce")
            Dim views = FindCombo(screen).Items.Cast(Of Object)().Select(Function(o) o.ToString()).ToList()
            Assert.IsTrue(views.Contains("Purchase history"))
            Assert.IsFalse(views.Contains("Production history"))
        End Using
        Using f As New frmNewPO(r.UserID)
            Assert.AreEqual("Record purchase", f.Text)
            Assert.IsTrue(HasButton(f, "Save purchase & add to stock"))
        End Using
    End Sub

    ''' The view picker specifically — the paged table has dropdowns of its own.
    Private Shared Function FindCombo(root As Control) As ComboBox
        For Each c As Control In root.Controls
            If TypeOf c Is ComboBox AndAlso DirectCast(c, ComboBox).Items.Contains("Stock by product") Then Return DirectCast(c, ComboBox)
            Dim inner = FindCombo(c)
            If inner IsNot Nothing Then Return inner
        Next
        Return Nothing
    End Function

    ' ===== price book =====

    <TestMethod>
    Public Sub Price_updates_are_saved_with_their_history_and_unchanged_lines_are_skipped()
        Dim user = NewAccount("Admin")
        Dim r = SignIn(user, "Passw0rd!2026", Company.CandidPurrfect)
        Dim a = TestDb.AddProduct("Price Book A " & Guid.NewGuid().ToString("N").Substring(0, 5), 1000D)
        Dim b = TestDb.AddProduct("Price Book B " & Guid.NewGuid().ToString("N").Substring(0, 5), 2000D)

        Dim saved = PriceBook.Apply({
            New PriceBook.PriceUpdate With {.ProductID = a, .Distributor = 1100D, .Wholesaler = 1200D, .Retail = 1300D},
            New PriceBook.PriceUpdate With {.ProductID = b, .Distributor = 2000D, .Wholesaler = 2000D, .Retail = 2000D}},
            r.UserID, "September review")

        Assert.AreEqual(1, saved, "B's prices didn't move, so nothing is written for it")
        Assert.AreEqual(1300D, Convert.ToDecimal(TestDb.Scalar("SELECT PriceRetail FROM Products WHERE ProductID = @p", TestDb.P("@p", a))))
        Assert.AreEqual(1300D, Convert.ToDecimal(TestDb.Scalar("SELECT SellingPrice FROM Products WHERE ProductID = @p", TestDb.P("@p", a))))
        Dim h = TestDb.Table("SELECT OldRetail, NewRetail, ChangedByUserID, Note FROM PriceChanges WHERE ProductID = @p", TestDb.P("@p", a))
        Assert.AreEqual(1, h.Rows.Count)
        Assert.AreEqual(1000D, Convert.ToDecimal(h.Rows(0)("OldRetail")))
        Assert.AreEqual(1300D, Convert.ToDecimal(h.Rows(0)("NewRetail")))
        Assert.AreEqual(r.UserID, Convert.ToInt32(h.Rows(0)("ChangedByUserID")))
        Assert.AreEqual("September review", Convert.ToString(h.Rows(0)("Note")))
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM PriceChanges WHERE ProductID = @p", TestDb.P("@p", b)))
        Assert.IsTrue(PriceBook.LastUpdated().HasValue)

        ' A second review stacks on the first.
        PriceBook.Apply({New PriceBook.PriceUpdate With {.ProductID = a, .Distributor = 1100D, .Wholesaler = 1200D, .Retail = 1400D}}, r.UserID, Nothing)
        Assert.AreEqual(2, TestDb.Count("SELECT COUNT(*) FROM PriceChanges WHERE ProductID = @p", TestDb.P("@p", a)))
    End Sub

    <TestMethod>
    Public Sub A_negative_price_saves_nothing_at_all()
        Dim r = SignIn(NewAccount("Admin"), "Passw0rd!2026", Company.CandidPurrfect)
        Dim a = TestDb.AddProduct("Negative Guard " & Guid.NewGuid().ToString("N").Substring(0, 5), 1000D)
        Try
            PriceBook.Apply({New PriceBook.PriceUpdate With {.ProductID = a, .Distributor = -1D, .Wholesaler = 1D, .Retail = 1D}}, r.UserID, Nothing)
            Assert.Fail("a negative price must be refused")
        Catch ex As ArgumentException
        End Try
        Assert.AreEqual(1000D, Convert.ToDecimal(TestDb.Scalar("SELECT PriceRetail FROM Products WHERE ProductID = @p", TestDb.P("@p", a))))
    End Sub

    <TestMethod>
    Public Sub Percentage_changes_round_to_the_nearest_naira_step()
        Assert.AreEqual(11550D, PriceBook.Adjusted(11000D, 5D, 50D))
        Assert.AreEqual(10500D, PriceBook.Adjusted(10000D, 5D, 0D))
        Assert.AreEqual(9000D, PriceBook.Adjusted(10000D, -10D, 100D))
        Assert.AreEqual(1240D, PriceBook.Adjusted(1234D, 0.5D, 10D))
        Assert.AreEqual(0D, PriceBook.Adjusted(100D, -90D, 50D), "never below zero")
    End Sub

    <TestMethod>
    Public Sub A_long_price_list_is_added_in_one_go_with_codes_barcodes_and_history()
        Dim r = SignIn(NewAccount("Admin"), "Passw0rd!2026", Company.CandidPurrfect)
        Dim tag = Guid.NewGuid().ToString("N").Substring(0, 5)
        Dim category = TestDb.Count("SELECT MIN(CategoryID) FROM Categories")
        Dim typed = Enumerable.Range(1, 25).Select(Function(i) New PriceBook.NewProduct With {
            .Name = $"Listed {tag} {i}", .CategoryID = category, .Unit = "Pack",
            .Distributor = 1000D + i, .Wholesaler = 1100D + i, .Retail = 1200D + i}).ToList()
        typed.Add(New PriceBook.NewProduct With {.Name = "   ", .CategoryID = category})   ' a blank row is ignored

        Dim ids = PriceBook.AddProducts(typed, r.UserID, "Opening price list")

        Assert.AreEqual(25, ids.Count)
        Dim rows = TestDb.Table($"SELECT ProductID, SKU, Barcode, PriceRetail FROM Products WHERE ProductID IN ({String.Join(",", ids)})")
        Assert.AreEqual(25, rows.AsEnumerable().Select(Function(x) Convert.ToString(x("SKU"))).Distinct().Count(), "each product has its own code")
        Assert.IsTrue(rows.AsEnumerable().All(Function(x) Convert.ToString(x("SKU")).StartsWith("CP-")), "Candid product codes")
        Assert.AreEqual(25, rows.AsEnumerable().Select(Function(x) Convert.ToString(x("Barcode"))).Distinct().Count(), "each product has its own barcode")
        Assert.IsTrue(rows.AsEnumerable().All(Function(x) Barcodes.ReadScan(Convert.ToString(x("Barcode"))).Code <> ""))
        Assert.AreEqual(25, TestDb.Count($"SELECT COUNT(*) FROM PriceChanges WHERE ProductID IN ({String.Join(",", ids)}) AND Note = 'Opening price list'"))
        Assert.AreEqual(25, PriceLists.Lines("Retailer", includeOutOfStock:=True).AsEnumerable().
                              Count(Function(x) Convert.ToString(x("Product")).StartsWith($"Listed {tag} ")), "they're on the price list straight away")
    End Sub

    <TestMethod>
    Public Sub A_product_already_on_the_list_is_refused_and_nothing_is_added()
        Dim r = SignIn(NewAccount("Admin"), "Passw0rd!2026", Company.CandidPurrfect)
        Dim tag = Guid.NewGuid().ToString("N").Substring(0, 5)
        Dim category = TestDb.Count("SELECT MIN(CategoryID) FROM Categories")
        PriceBook.AddProducts({New PriceBook.NewProduct With {.Name = "Dup " & tag, .CategoryID = category, .Retail = 5D, .Wholesaler = 5D, .Distributor = 5D}}, r.UserID, Nothing)

        Try
            PriceBook.AddProducts({
                New PriceBook.NewProduct With {.Name = "Fresh " & tag, .CategoryID = category, .Retail = 5D, .Wholesaler = 5D, .Distributor = 5D},
                New PriceBook.NewProduct With {.Name = "Dup " & tag, .CategoryID = category, .Retail = 6D, .Wholesaler = 6D, .Distributor = 6D}}, r.UserID, Nothing)
            Assert.Fail("a product already listed must be refused")
        Catch ex As ArgumentException
            StringAssert.Contains(ex.Message, "Dup " & tag)
        End Try
        Assert.AreEqual(0, TestDb.Count("SELECT COUNT(*) FROM Products WHERE Name = @n", TestDb.P("@n", "Fresh " & tag)), "all or nothing")
    End Sub

    <TestMethod>
    Public Sub Candid_price_lists_carry_a_barcode()
        SignIn(NewAccount("Admin"), "Passw0rd!2026", Company.CandidPurrfect)
        Dim doc = PriceLists.Build("Retailer", includeOutOfStock:=True)
        Dim blocks = DirectCast(GetType(DocPrinter).GetField("_blocks", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance).GetValue(doc), IEnumerable)
        Assert.IsTrue(blocks.Cast(Of Object)().Any(Function(b) b.GetType().Name = "BarcodeBlock"), "the price list must carry its barcode")
        Assert.AreEqual($"CandidPurrfect-PL-{Date.Today:ddMMyyyy}", PriceLists.ReferenceFor(Date.Today))
    End Sub

    <TestMethod>
    Public Sub Candid_receipts_carry_a_barcode_of_their_own_number()
        Dim r = SignIn(NewAccount("Admin"), "Passw0rd!2026", Company.CandidPurrfect)
        Dim product = TestDb.AddProduct("Receipt Barcode Feed " & Guid.NewGuid().ToString("N").Substring(0, 5), 1000D)
        Dim warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")
        TestDb.Exec("INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p, @w, @b, 20)",
                    TestDb.P("@p", product, "@w", warehouse, "@b", "RB-" & Guid.NewGuid().ToString("N").Substring(0, 6)))
        Dim customer = TestDb.AddCustomer("Receipt Barcode Customer")
        Dim req As New Sales.SaleRequest() With {
            .CustomerID = customer, .CustomerName = "Receipt Barcode Customer", .CustomerType = "Retailer", .PriceTier = "Retailer",
            .WarehouseID = warehouse, .PaymentMethod = "Cash", .PaidNow = 2000D}
        req.Lines.Add(New Sales.SaleLine() With {.ProductID = product, .ProductName = "Feed", .Quantity = 2, .UnitPrice = 1000D, .UnitCost = 600D})
        Dim saved = Sales.Save(req, r.UserID)
        Assert.IsTrue(saved.InvoiceNumber.StartsWith("CandidPurrfect-"))

        Using f As New frmInvoiceReceipt(saved.InvoiceID)
            Dim flags = Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance
            Dim doc = f.GetType().GetMethod("BuildReceiptDoc", flags).Invoke(f, Nothing)
            Dim blocks = DirectCast(GetType(DocPrinter).GetField("_blocks", flags).GetValue(doc), IEnumerable)
            Assert.IsTrue(blocks.Cast(Of Object)().Any(Function(b) b.GetType().Name = "BarcodeBlock"),
                          "a Candid receipt must actually carry the barcode, not just build")
        End Using
    End Sub

    <TestMethod>
    Public Sub The_price_list_screen_opens_for_candid()
        Dim r = SignIn(NewAccount("Warehouse Clerk"), "Passw0rd!2026", Company.CandidPurrfect)
        Using screen As New ucPriceList(r.UserID, isAdmin:=False)
            Assert.IsFalse(HasButton(screen, "Update prices…"), "clerks can see and send the price list, not change it")
            Assert.IsTrue(HasButton(screen, "Send price list…"))
        End Using
        Using screen As New ucPriceList(r.UserID, isAdmin:=True)
            Assert.IsTrue(HasButton(screen, "Update prices…"))
        End Using
        Using f As New frmPriceUpdate(r.UserID)
            Assert.IsNotNull(f)
        End Using
    End Sub

    ' ===== price lists =====

    <TestMethod>
    Public Sub Only_candid_offers_the_price_list_button()
        Company.Use(Company.ChewyPets)
        Assert.IsFalse(HasButton(New ucInventory(1, isAdminUser:=True), "Send price list…"))
        Assert.IsFalse(HasButton(New ucCustomers(1), "Send price list…"))

        Company.Use(Company.CandidPurrfect)
        Assert.IsTrue(HasButton(New ucInventory(1, isAdminUser:=False), "Send price list…"), "clerks send price lists from Inventory")
        Assert.IsTrue(HasButton(New ucCustomers(1), "Send price list…"))
    End Sub

    Private Shared Function HasButton(root As Control, text As String) As Boolean
        For Each c As Control In root.Controls
            If TypeOf c Is Button AndAlso c.Text = text Then Return True
            If HasButton(c, text) Then Return True
        Next
        Return False
    End Function

    <TestMethod>
    Public Sub The_price_list_shows_the_customers_tier_and_hides_what_is_unpriced_or_unavailable()
        SignIn(NewAccount("Admin"), "Passw0rd!2026", Company.CandidPurrfect)
        Dim tag = Guid.NewGuid().ToString("N").Substring(0, 6)
        Dim stocked = TestDb.AddProduct("Cat Chow " & tag)
        TestDb.Exec("UPDATE Products SET PriceDistributor = 9000, PriceWholesaler = 9500, PriceRetail = 10000 WHERE ProductID = @p", TestDb.P("@p", stocked))
        TestDb.Exec("INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p, (SELECT MIN(WarehouseID) FROM Warehouses), @b, 12)",
                    TestDb.P("@p", stocked, "@b", "PL-" & tag))
        Dim empty = TestDb.AddProduct("Kitten Milk " & tag)
        Dim unpriced = TestDb.AddProduct("Unpriced Treat " & tag)
        TestDb.Exec("UPDATE Products SET PriceWholesaler = 0 WHERE ProductID = @p", TestDb.P("@p", unpriced))

        Dim names = Function(t As DataTable) t.AsEnumerable().Select(Function(r) Convert.ToString(r("Product"))).ToList()
        Dim wholesale = PriceLists.Lines("Wholesaler", includeOutOfStock:=False)
        Dim line = wholesale.AsEnumerable().Single(Function(r) Convert.ToString(r("Product")) = "Cat Chow " & tag)
        Assert.AreEqual(9500D, Convert.ToDecimal(line("Price")), "a wholesaler sees wholesale prices")
        Assert.IsFalse(names(wholesale).Contains("Kitten Milk " & tag), "out of stock is left off unless asked for")
        Assert.IsTrue(names(PriceLists.Lines("Wholesaler", includeOutOfStock:=True)).Contains("Kitten Milk " & tag))
        Assert.IsFalse(names(PriceLists.Lines("Wholesaler", includeOutOfStock:=True)).Contains("Unpriced Treat " & tag),
                       "₦0.00 on a price list reads as free")

        Assert.AreEqual("Retailer", PriceLists.TierFor("Walk-in"))
        Assert.AreEqual("Distributor", PriceLists.TierFor("Distributor"))
    End Sub

    <TestMethod>
    Public Sub The_price_list_document_renders()
        SignIn(NewAccount("Warehouse Clerk"), "Passw0rd!2026", Company.CandidPurrfect)
        Dim d = PriceLists.Build("Retailer", "Mama Tobi Pets", includeOutOfStock:=True)
        Assert.IsTrue(d.PageCount() >= 1)
        StringAssert.Contains(d.DocTitle, "Candid Purrfect")
        StringAssert.Contains(d.DocTitle, "Mama Tobi Pets")
    End Sub

    <TestMethod>
    Public Sub The_email_opens_ready_to_send_with_candids_details()
        Company.Use(Company.CandidPurrfect)
        Dim body = PriceLists.EmailBody("Wholesaler", "Mama Tobi Pets")
        StringAssert.Contains(body, "Dear Mama Tobi Pets")
        StringAssert.Contains(body, "0097166161")
        StringAssert.Contains(body, "Candid Purrfect Pets Company Ltd")

        Dim link = PriceLists.MailtoLink("orders@mamatobi.ng", PriceLists.EmailSubject(), body)
        Assert.IsTrue(link.StartsWith("mailto:orders@mamatobi.ng?subject="), link)
        Assert.IsFalse(link.Contains(" "), "spaces must be encoded or the email app cuts the message short")
        StringAssert.Contains(link, "%0D%0A")

        Assert.IsTrue(PriceLists.IsPlausibleEmail("orders@mamatobi.ng"))
        Assert.IsFalse(PriceLists.IsPlausibleEmail("orders mamatobi"))
        Assert.IsFalse(PriceLists.IsPlausibleEmail("a@b"))
    End Sub

End Class
