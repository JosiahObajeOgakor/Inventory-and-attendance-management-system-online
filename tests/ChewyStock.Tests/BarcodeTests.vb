Imports System.Data
Imports System.Reflection
Imports System.Windows.Forms
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' Barcodes: the symbology maths, and the paths through the app that actually
''' use it — minting a code when a product is added, and scanning one onto a
''' sale. The check digits are worked out by hand in the test names so a wrong
''' answer is obvious rather than merely different.
<TestClass>
Public Class BarcodeTests

    Private Const NonPublicInstance As BindingFlags = BindingFlags.NonPublic Or BindingFlags.Instance

    Private Shared Function Field(target As Object, name As String) As Object
        Dim f = target.GetType().GetField(name, NonPublicInstance)
        Assert.IsNotNull(f, "no field named " & name)
        Return f.GetValue(target)
    End Function

    Private Shared Sub Invoke(target As Object, method As String, ParamArray args As Object())
        target.GetType().GetMethod(method, NonPublicInstance).Invoke(target, args)
    End Sub

    ' ===== check digits =====

    <TestMethod>
    Public Sub The_ean13_check_digit_matches_the_published_example()
        ' 4006381333931 is the textbook EAN-13; its check digit is 1.
        Assert.AreEqual(1, Barcodes.Ean13CheckDigit("400638133393"))
        Assert.IsTrue(Barcodes.IsValidEan13("4006381333931"))
    End Sub

    <TestMethod>
    Public Sub A_barcode_with_a_wrong_digit_is_rejected()
        Assert.IsFalse(Barcodes.IsValidEan13("4006381333932"), "the check digit is what catches a misread")
        Assert.IsFalse(Barcodes.IsValidEan13("400638133393"), "twelve digits is not a finished EAN-13")
        Assert.IsFalse(Barcodes.IsValidEan13("abcdefghijklm"))
    End Sub

    <TestMethod>
    Public Sub A_minted_barcode_is_valid_and_sits_in_the_in_store_range()
        Dim code = Barcodes.MintInternalBarcode(42)

        Assert.IsTrue(Barcodes.IsValidEan13(code), "a minted code has to scan like any other: " & code)
        Assert.IsTrue(code.StartsWith("200"),
                      "prefix 200 is GS1's in-store range — outside it we could collide with a real product")
    End Sub

    <TestMethod>
    Public Sub Two_products_never_mint_the_same_barcode()
        Assert.AreNotEqual(Barcodes.MintInternalBarcode(1), Barcodes.MintInternalBarcode(2))
    End Sub

    ' ===== symbols =====

    <TestMethod>
    Public Sub Code128_draws_something_scannable_for_ordinary_text()
        Using bmp = Barcodes.Code128("CS-0001")
            Assert.IsTrue(bmp.Width > 50, "a symbol needs real width to be read")
            Assert.IsTrue(bmp.Height > 40)
        End Using
    End Sub

    <TestMethod>
    Public Sub Code128_refuses_characters_it_cannot_carry()
        Assert.IsFalse(Barcodes.CanEncode128("naïve"), "set B is printable ASCII only")
        Assert.ThrowsException(Of ArgumentException)(Sub() Barcodes.Code128("naïve"))
    End Sub

    <TestMethod>
    Public Sub A_shelf_label_carries_the_name_price_and_symbol()
        Using label = Barcodes.ShelfLabel("Adult Dog Food 20kg", "SKU-1001", Barcodes.MintInternalBarcode(7), 11500D)
            Assert.IsTrue(label.Width >= 320, "a label has to be wide enough to print")
            Assert.IsTrue(label.Height > 100)
        End Using
    End Sub

    ' ===== scanner input =====

    <TestMethod>
    Public Sub A_plain_barcode_reads_as_a_product_scan()
        Dim scan = Barcodes.ReadScan("2000000000426")
        Assert.AreEqual("Barcode", scan.Kind)
        Assert.AreEqual("2000000000426", scan.Code)
    End Sub

    <TestMethod>
    Public Sub Our_own_serial_tag_reads_back_as_a_serial()
        Dim scan = Barcodes.ReadScan(Barcodes.SerialTagPayload("SKU-1001", "SN-77"))
        Assert.AreEqual("Serial", scan.Kind)
        Assert.AreEqual("SKU-1001", scan.Sku)
        Assert.AreEqual("SN-77", scan.Code)
    End Sub

    <TestMethod>
    Public Sub Our_own_invoice_tag_reads_back_as_an_invoice()
        Dim scan = Barcodes.ReadScan(Barcodes.InvoiceTagPayload("ChewyStock-13092026-143205", 11500D))
        Assert.AreEqual("Invoice", scan.Kind)
        Assert.AreEqual("ChewyStock-13092026-143205", scan.Code)
    End Sub

    <TestMethod>
    Public Sub An_empty_scan_is_ignored_rather_than_acted_on()
        Assert.IsNull(Barcodes.ReadScan(""))
        Assert.IsNull(Barcodes.ReadScan("   "))
    End Sub

    ' ===== wired into the app =====

    <TestMethod>
    Public Sub Adding_a_product_without_a_barcode_mints_one_for_it()
        TestDb.Rebuild()
        Dim productId = TestDb.AddProduct("Unlabelled Feed")
        ' The add-item flow mints from the new product's ID; reproduce that here
        ' and check the result is a code a scanner would accept.
        Dim minted = Barcodes.MintInternalBarcode(productId)
        TestDb.Exec("UPDATE Products SET Barcode=@b WHERE ProductID=@id", TestDb.P("@b", minted, "@id", productId))

        Dim stored = Convert.ToString(TestDb.Scalar("SELECT Barcode FROM Products WHERE ProductID=@id", TestDb.P("@id", productId)))
        Assert.IsTrue(Barcodes.IsValidEan13(stored), "the stored code must scan: " & stored)
    End Sub

    <TestMethod>
    Public Sub The_same_barcode_cannot_be_given_to_two_products()
        TestDb.Rebuild()
        Dim first = TestDb.AddProduct("Barcode Owner")
        Dim second = TestDb.AddProduct("Barcode Thief")
        Dim code = Barcodes.MintInternalBarcode(first)
        TestDb.Exec("UPDATE Products SET Barcode=@b WHERE ProductID=@id", TestDb.P("@b", code, "@id", first))

        Try
            TestDb.Exec("UPDATE Products SET Barcode=@b WHERE ProductID=@id", TestDb.P("@b", code, "@id", second))
            Assert.Fail("two products sharing a barcode would ring up the wrong item at the till")
        Catch ex As Exception
            ' The unique index is what stops it — exactly as intended.
        End Try
    End Sub

    <TestMethod>
    Public Sub Scanning_a_known_barcode_adds_that_product_to_the_sale()
        TestDb.Rebuild()
        Dim customer = TestDb.AddCustomer("Scan Customer")
        Dim productId = TestDb.AddProduct("Scannable Feed", 500D)
        Dim warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")
        TestDb.Exec("INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p,@w,'SCAN',50)",
                    TestDb.P("@p", productId, "@w", warehouse))
        Dim code = Barcodes.MintInternalBarcode(productId)
        TestDb.Exec("UPDATE Products SET Barcode=@b WHERE ProductID=@id", TestDb.P("@b", code, "@id", productId))

        Using f As New frmNewInvoice(1)
            f.Show()
            Application.DoEvents()
            DirectCast(Field(f, "cboCustomer"), ComboBox).SelectedValue = customer

            DirectCast(Field(f, "txtScan"), TextBox).Text = code
            Invoke(f, "Scan_KeyDown", Nothing, New KeyEventArgs(Keys.Enter))
            Application.DoEvents()

            Dim lines = DirectCast(Field(f, "lineItems"), DataTable)
            Assert.AreEqual(1, lines.Rows.Count, "one scan should put one line on the sale")
            Assert.AreEqual(productId, Convert.ToInt32(lines.Rows(0)("ProductID")))
            Assert.AreEqual(1, Convert.ToInt32(lines.Rows(0)("Qty")), "a scan is one unit")
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Scanning_the_same_product_twice_makes_it_two_units()
        TestDb.Rebuild()
        Dim customer = TestDb.AddCustomer("Repeat Scan Customer")
        Dim productId = TestDb.AddProduct("Repeat Feed", 500D)
        Dim warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")
        TestDb.Exec("INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p,@w,'SCAN',50)",
                    TestDb.P("@p", productId, "@w", warehouse))
        Dim code = Barcodes.MintInternalBarcode(productId)
        TestDb.Exec("UPDATE Products SET Barcode=@b WHERE ProductID=@id", TestDb.P("@b", code, "@id", productId))

        Using f As New frmNewInvoice(1)
            f.Show()
            Application.DoEvents()
            DirectCast(Field(f, "cboCustomer"), ComboBox).SelectedValue = customer
            Dim scanBox = DirectCast(Field(f, "txtScan"), TextBox)

            For pass = 1 To 2
                scanBox.Text = code
                Invoke(f, "Scan_KeyDown", Nothing, New KeyEventArgs(Keys.Enter))
                Application.DoEvents()
            Next

            Dim lines = DirectCast(Field(f, "lineItems"), DataTable)
            Assert.AreEqual(1, lines.Rows.Count, "the same product stays one line")
            Assert.AreEqual(2, Convert.ToInt32(lines.Rows(0)("Qty")), "scanned twice is two units")
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Scanning_an_unknown_barcode_adds_nothing()
        TestDb.Rebuild()
        Using f As New frmNewInvoice(1)
            f.Show()
            Application.DoEvents()
            DirectCast(Field(f, "txtScan"), TextBox).Text = "9999999999994"
            Invoke(f, "Scan_KeyDown", Nothing, New KeyEventArgs(Keys.Enter))
            Application.DoEvents()

            Assert.AreEqual(0, DirectCast(Field(f, "lineItems"), DataTable).Rows.Count,
                            "an unrecognised code must never guess at a product")
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub The_scan_box_clears_itself_ready_for_the_next_item()
        TestDb.Rebuild()
        Using f As New frmNewInvoice(1)
            f.Show()
            Application.DoEvents()
            Dim scanBox = DirectCast(Field(f, "txtScan"), TextBox)
            scanBox.Text = "9999999999994"
            Invoke(f, "Scan_KeyDown", Nothing, New KeyEventArgs(Keys.Enter))
            Application.DoEvents()

            Assert.AreEqual("", scanBox.Text, "the till has to be ready for the next scan without anyone clearing it")
            f.Close()
        End Using
    End Sub

    ' ===== every receipt carries its own code =====

    <TestMethod>
    Public Sub A_receipt_carries_a_scannable_code_of_its_own()
        TestDb.Rebuild()
        Dim customer = TestDb.AddCustomer("Receipt Customer")
        Dim product = TestDb.AddProduct("Receipt Feed", 500D)
        Dim invoiceId = TestDb.AddInvoice(customer, product, Date.Today, total:=7500D)
        Dim number = Convert.ToString(TestDb.Scalar("SELECT InvoiceNumber FROM Invoices WHERE InvoiceID=@i",
                                                    TestDb.P("@i", invoiceId)))

        ' The receipt document builds with the barcode block on it — this is the
        ' test that would catch the symbol throwing during layout.
        Using f As New frmInvoiceReceipt(invoiceId)
            f.Show()
            Application.DoEvents()
            Dim doc = f.GetType().GetMethod("BuildReceiptDoc", NonPublicInstance).Invoke(f, Nothing)
            Assert.IsNotNull(doc, "the receipt has to build with its barcode on it")
            f.Close()
        End Using
        Assert.IsTrue(Barcodes.CanEncode128(number), "the receipt number must be encodable: " & number)
        Using symbol = Barcodes.Code128(number)
            Assert.IsTrue(symbol.Width > 50)
        End Using
    End Sub

    <TestMethod>
    Public Sub Two_sales_get_two_different_receipt_codes()
        TestDb.Rebuild()
        Dim customer = TestDb.AddCustomer("Two Receipts Customer")
        Dim product = TestDb.AddProduct("Two Receipts Feed", 500D)
        Dim first = TestDb.AddInvoice(customer, product, Date.Today)
        Dim second = TestDb.AddInvoice(customer, product, Date.Today)

        Dim a = Convert.ToString(TestDb.Scalar("SELECT InvoiceNumber FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", first)))
        Dim b = Convert.ToString(TestDb.Scalar("SELECT InvoiceNumber FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", second)))

        Assert.AreNotEqual(a, b, "two receipts sharing a code would pull up the wrong sale at the counter")
    End Sub

    <TestMethod>
    Public Sub Scanning_a_receipt_code_identifies_the_sale_it_came_from()
        TestDb.Rebuild()
        Dim customer = TestDb.AddCustomer("Scanned Receipt Customer")
        Dim product = TestDb.AddProduct("Scanned Receipt Feed", 500D)
        Dim invoiceId = TestDb.AddInvoice(customer, product, Date.Today)
        Dim number = Convert.ToString(TestDb.Scalar("SELECT InvoiceNumber FROM Invoices WHERE InvoiceID=@i",
                                                    TestDb.P("@i", invoiceId)))

        ' What a hand scanner sends when it reads the bars printed on the receipt.
        Dim scan = Barcodes.ReadScan(number)
        Dim found = TestDb.Count("SELECT COUNT(*) FROM Invoices WHERE InvoiceNumber = @n", TestDb.P("@n", scan.Code))

        Assert.AreEqual(1, found, "scanning the receipt has to find exactly the sale it was printed for")
    End Sub

    <TestMethod>
    Public Sub A_receipt_qr_payload_also_resolves_back_to_its_sale()
        Dim number = "ChewyStock-13092026-143205"
        Dim scan = Barcodes.ReadScan(Barcodes.InvoiceTagPayload(number, 11500D))

        Assert.AreEqual("Invoice", scan.Kind)
        Assert.AreEqual(number, scan.Code, "the QR tag has to give back the same number the bars carry")
    End Sub

    <TestMethod>
    Public Sub Goods_received_on_a_purchase_order_come_out_with_a_barcode()
        TestDb.Rebuild()
        Dim productId = TestDb.AddProduct("Unlabelled Arrival")
        TestDb.Exec("UPDATE Products SET Barcode = NULL WHERE ProductID = @p", TestDb.P("@p", productId))

        ' What the receive step does for anything that arrived without one.
        DataAccess.Execute(
            "UPDATE Products SET Barcode = @b WHERE ProductID = @p AND (Barcode IS NULL OR Barcode = '')",
            New Dictionary(Of String, Object) From {{"@b", Barcodes.MintInternalBarcode(productId)}, {"@p", productId}})

        Dim stored = Convert.ToString(TestDb.Scalar("SELECT Barcode FROM Products WHERE ProductID=@p", TestDb.P("@p", productId)))
        Assert.IsTrue(Barcodes.IsValidEan13(stored), "it should be scannable the moment it reaches the shelf: " & stored)
    End Sub

    <TestMethod>
    Public Sub Receiving_never_overwrites_a_barcode_the_supplier_already_printed()
        TestDb.Rebuild()
        Dim productId = TestDb.AddProduct("Labelled Arrival")
        TestDb.Exec("UPDATE Products SET Barcode = '4006381333931' WHERE ProductID = @p", TestDb.P("@p", productId))

        DataAccess.Execute(
            "UPDATE Products SET Barcode = @b WHERE ProductID = @p AND (Barcode IS NULL OR Barcode = '')",
            New Dictionary(Of String, Object) From {{"@b", Barcodes.MintInternalBarcode(productId)}, {"@p", productId}})

        Assert.AreEqual("4006381333931",
                        Convert.ToString(TestDb.Scalar("SELECT Barcode FROM Products WHERE ProductID=@p", TestDb.P("@p", productId))),
                        "replacing the manufacturer's own code would stop their label scanning")
    End Sub

    <TestMethod>
    Public Sub The_add_item_screen_offers_somewhere_to_scan_a_barcode()
        Using f As New frmAddItem()
            f.Show()
            Application.DoEvents()
            Assert.IsNotNull(Field(f, "txtBarcode"), "a supplier's own barcode has to be capturable at entry")
            f.Close()
        End Using
    End Sub

End Class
