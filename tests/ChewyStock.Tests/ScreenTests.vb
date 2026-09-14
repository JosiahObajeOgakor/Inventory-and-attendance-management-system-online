Imports System.Data
Imports System.Drawing
Imports System.Reflection
Imports System.Windows.Forms
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports StockDesk

''' The screens themselves: they build, they show the controls people need, and
''' the sale screen really does save on the date that was chosen.
<TestClass>
Public Class ScreenTests

    Private Const NonPublicInstance As BindingFlags = BindingFlags.NonPublic Or BindingFlags.Instance

    Private Shared Function Field(target As Object, name As String) As Object
        Dim f = target.GetType().GetField(name, NonPublicInstance)
        Assert.IsNotNull(f, "no field named " & name)
        Return f.GetValue(target)
    End Function

    Private Shared Sub Invoke(target As Object, method As String, ParamArray args As Object())
        target.GetType().GetMethod(method, NonPublicInstance).Invoke(target, args)
    End Sub

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

    ' ===== activation =====

    <TestMethod>
    Public Sub Activation_screen_shows_payment_machine_id_and_vendor_code_entry()
        Using f As New frmActivation()
            f.Show()
            Application.DoEvents()
            Assert.IsTrue(HasText(f, "Pay now"), "the single Pay now button is the main action")
            Assert.IsTrue(HasText(f, "I've already paid"))
            Assert.IsTrue(HasText(f, Licensing.MachineId), "the Machine ID must be on screen to read out")
            Assert.IsTrue(HasText(f, "installation code or offline key"))
            Assert.IsTrue(HasText(f, "Skip for now"))
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Activation_screen_hides_the_vendor_code_box_until_it_is_asked_for()
        Using f As New frmActivation()
            f.Show()
            Application.DoEvents()
            Dim codeRow = DirectCast(Field(f, "codeRow"), Control)
            Assert.IsFalse(codeRow.Visible, "the code box is vendor-only — hidden by default")
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Activation_screen_refuses_an_empty_email_before_calling_out()
        Using f As New frmActivation()
            f.Show()
            Application.DoEvents()
            DirectCast(Field(f, "txtEmail"), TextBox).Text = "not-an-email"
            Invoke(f, "Pay_Click", Nothing, EventArgs.Empty)
            Dim status = DirectCast(Field(f, "lblStatus"), Label)
            Assert.IsTrue(status.Text.IndexOf("valid email", StringComparison.OrdinalIgnoreCase) >= 0,
                          "expected a validation message, got: " & status.Text)
            f.Close()
        End Using
    End Sub

    ' ===== clerk welcome =====

    <TestMethod>
    Public Sub The_welcome_clip_is_for_clerks_only()
        Assert.IsFalse(frmClerkWelcome.AppliesTo("Admin"), "admins go straight into the app")
        Assert.IsTrue(frmClerkWelcome.AppliesTo("Warehouse Clerk"))
        Assert.IsTrue(frmClerkWelcome.AppliesTo("Clerk"))
    End Sub

    <TestMethod>
    Public Sub The_welcome_clip_ships_with_the_app()
        Assert.IsTrue(IO.File.Exists(frmClerkWelcome.VideoPath()), "landingvideo.mp4 must be installed alongside the app")
    End Sub

    ''' Each welcome-screen scenario needs today's Attendance row for this user
    ''' clear beforehand — otherwise an earlier test's check-in makes the screen
    ''' think she's already checked in today.
    Private Shared Sub ClearTodayAttendance(userId As Integer)
        TestDb.Exec("DELETE FROM AttendanceEvents WHERE UserID=@u AND WorkDate=@d", TestDb.P("@u", userId, "@d", Date.Today))
    End Sub

    ''' Clicking anywhere, pressing a key, or the safety timeout must NOT hand
    ''' over to the app before she has checked in — attendance would otherwise
    ''' be easy to skip past without noticing.
    <TestMethod>
    Public Sub The_welcome_screen_will_not_close_before_she_checks_in()
        Dim userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
        ClearTodayAttendance(userId)
        Try
            Using f As New frmClerkWelcome(userId, "Aisha")
                f.Show()
                Application.DoEvents()
                Invoke(f, "TryFinish")
                Application.DoEvents()
                Assert.IsFalse(f.IsDisposed, "must not hand over until she has checked in")
                Assert.IsFalse(f.CheckedIn)
                f.Close()
            End Using
        Finally
            ClearTodayAttendance(userId)
        End Try
    End Sub

    ''' Clicking "Check In" logs today's time, then the screen hands over to the app.
    <TestMethod>
    Public Sub Checking_in_from_the_welcome_screen_logs_it_and_hands_over()
        Dim userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
        ClearTodayAttendance(userId)
        Try
            Using f As New frmClerkWelcome(userId, "Aisha")
                f.Show()
                Application.DoEvents()
                Invoke(f, "CheckIn_Click", Nothing, EventArgs.Empty)
                Application.DoEvents()
                Assert.IsTrue(f.CheckedIn)

                For i = 1 To 30
                    Application.DoEvents()
                    Threading.Thread.Sleep(100)
                    If f.IsDisposed Then Exit For
                Next
                Assert.IsTrue(f.IsDisposed, "checking in must hand over to the app")
            End Using

            Dim at = Attendance.TodayCheckIn(userId)
            Assert.IsTrue(at.HasValue, "the check-in must be on record for the admin to see")
        Finally
            ClearTodayAttendance(userId)
        End Try
    End Sub

    ''' "Sign out instead" logs it (so admin can see she signed in and back out
    ''' without clocking in) and returns to login rather than opening the app.
    <TestMethod>
    Public Sub Declining_to_check_in_signs_her_out_instead_of_opening_the_app()
        Dim userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
        ClearTodayAttendance(userId)
        Try
            Using f As New frmClerkWelcome(userId, "Aisha")
                f.Show()
                Application.DoEvents()
                Invoke(f, "SignOut_Click", Nothing, EventArgs.Empty)
                Application.DoEvents()
                Assert.IsTrue(f.SignedOut)
                Assert.IsFalse(f.CheckedIn)
                Assert.IsTrue(f.IsDisposed, "declining must still close the welcome screen")
            End Using
        Finally
            ClearTodayAttendance(userId)
        End Try
    End Sub

    <TestMethod>
    Public Sub Showing_the_welcome_for_an_admin_does_nothing_and_returns_true()
        Dim before = Application.OpenForms.Count
        Dim proceeded = frmClerkWelcome.ShowFor(1, "Admin", "Josiah")
        Assert.AreEqual(before, Application.OpenForms.Count)
        Assert.IsTrue(proceeded, "admins always proceed straight into the app")
    End Sub

    ' ===== inventory / production =====

    <TestMethod>
    Public Sub Inventory_offers_production_entry_and_a_production_history_view()
        Using host As New Form()
            Dim uc As New ucInventory(1, True)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            Assert.IsTrue(HasText(host, "Record production"), "clerks and admins add produced goods here")
            Dim view = DirectCast(Field(uc, "cboView"), ComboBox)
            Assert.IsTrue(view.Items.Cast(Of Object)().Any(Function(i) Convert.ToString(i) = "Production history"))
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Production_screen_defaults_to_today_and_a_dated_batch_number()
        Using f As New frmProduction(1)
            f.Show()
            Application.DoEvents()
            Dim [date] = DirectCast(Field(f, "dtpDate"), DateTimePicker)
            Dim batch = DirectCast(Field(f, "txtBatch"), TextBox)
            Assert.AreEqual(Date.Today, [date].Value.Date)
            Assert.AreEqual(Date.Today, [date].MaxDate.Date, "production cannot be dated in the future")
            Assert.AreEqual("PROD-" & Date.Today.ToString("yyMMdd"), batch.Text)
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Production_screen_saves_through_to_stock()
        Dim product = TestDb.AddProduct("Screen production feed")
        Dim warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")
        Using f As New frmProduction(1)
            f.Show()
            Application.DoEvents()
            DirectCast(Field(f, "cboProduct"), ComboBox).SelectedValue = product
            DirectCast(Field(f, "cboWarehouse"), ComboBox).SelectedValue = warehouse
            DirectCast(Field(f, "numQty"), NumericUpDown).Value = 35D
            DirectCast(Field(f, "dtpDate"), DateTimePicker).Value = Date.Today.AddDays(-2)
            DirectCast(Field(f, "txtBatch"), TextBox).Text = "PROD-SCREEN"
            Invoke(f, "Save_Click", Nothing, EventArgs.Empty)
        End Using

        Assert.AreEqual(35, Stock.QuantityInWarehouse(product, warehouse))
        Assert.AreEqual(Date.Today.AddDays(-2),
                        Convert.ToDateTime(TestDb.Scalar("SELECT MovementDate FROM StockMovements WHERE ProductID=@p", TestDb.P("@p", product))).Date)
    End Sub

    ' ===== main shell (nav bar) =====

    <TestMethod>
    Public Sub The_logo_never_overlaps_the_app_name_in_the_shell()
        Using main As New frmMain() With {.CurrentUserID = 1, .CurrentUserName = "Josiah Obaje", .CurrentUserRole = "Admin"}
            main.Show()
            Application.DoEvents()
            Dim logo = DirectCast(Field(main, "picLogo"), PictureBox)
            Dim brand = DirectCast(Field(main, "lblBrand"), Label)
            Dim companyLbl = DirectCast(Field(main, "lblCompany"), Label)
            ' Different controls sit under different parents (logo under brandLeft,
            ' captions under brandText) — compare in one shared coordinate space.
            Dim logoRightOnForm = main.PointToClient(logo.Parent.PointToScreen(New Point(logo.Bounds.Right, 0))).X
            Dim brandLeftOnForm = main.PointToClient(brand.Parent.PointToScreen(New Point(brand.Bounds.Left, 0))).X
            Assert.IsTrue(brandLeftOnForm >= logoRightOnForm,
                         $"the app name (x={brandLeftOnForm}) must start at or after the logo's right edge (x={logoRightOnForm})")
            If companyLbl.Visible Then
                Dim companyLeftOnForm = main.PointToClient(companyLbl.Parent.PointToScreen(New Point(companyLbl.Bounds.Left, 0))).X
                Assert.IsTrue(companyLeftOnForm >= logoRightOnForm, "the company caption must not sit under the logo either")
            End If
            main.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Buying_and_money_screens_are_grouped_under_two_tabs()
        Using main As New frmMain() With {.CurrentUserID = 1, .CurrentUserName = "Josiah Obaje", .CurrentUserRole = "Admin"}
            main.Show()
            Application.DoEvents()
            Dim tabs = DirectCast(Field(main, "_tabButtons"), List(Of Button))
            Dim keys = tabs.Select(Function(b) CStr(b.Tag)).ToList()

            For Each grouped In {"Purchases", "Suppliers", "Expenses", "Income", "Rebates", "Sales", "Receipts", "Waybill"}
                Assert.IsFalse(keys.Contains(grouped), grouped & " belongs in a menu now, not its own tab: " & String.Join(", ", keys))
            Next
            For Each topLevel In {"Dashboard", "Inventory", "Customers", "Finance", "Employees"}
                Assert.IsTrue(keys.Contains(topLevel), topLevel & " must stay a tab: " & String.Join(", ", keys))
            Next
            main.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Pointing_at_a_grouped_tab_drops_its_menu_without_a_click()
        Using main As New frmMain() With {.CurrentUserID = 1, .CurrentUserName = "Josiah Obaje", .CurrentUserRole = "Admin"}
            main.Show()
            Application.DoEvents()
            ' The nav only responds once the app is activated; a test copy isn't.
            main.GetType().GetField("_activated", NonPublicInstance).SetValue(main, True)

            Dim tabs = DirectCast(Field(main, "_tabButtons"), List(Of Button))
            Dim inventory = tabs.First(Function(b) CStr(b.Tag) = "Inventory")
            inventory.GetType().GetMethod("OnMouseEnter", NonPublicInstance).Invoke(inventory, {EventArgs.Empty})
            Application.DoEvents()

            Dim menu = TryCast(Field(main, "_openMenu"), ContextMenuStrip)
            Assert.IsNotNull(menu, "hovering Inventory should drop its menu — no click first")
            Dim labels = menu.Items.Cast(Of ToolStripItem)().Select(Function(i) i.Text).ToList()
            For Each grouped In {"Purchases", "Suppliers"}
                Assert.IsTrue(labels.Contains(grouped), grouped & " should be in the hover menu: " & String.Join(" | ", labels))
            Next
            Assert.IsTrue(labels.Contains("Inventory"), "the tab's own screen stays reachable from its menu")
            menu.Close()
            main.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub A_grouped_screen_keeps_its_parent_tab_highlighted()
        Using main As New frmMain() With {.CurrentUserID = 1, .CurrentUserName = "Josiah Obaje", .CurrentUserRole = "Admin"}
            main.Show()
            Application.DoEvents()
            Invoke(main, "ShowScreen", "Suppliers")
            Application.DoEvents()

            Dim tabs = DirectCast(Field(main, "_tabButtons"), List(Of Button))
            Dim inventory = tabs.First(Function(b) CStr(b.Tag) = "Inventory")
            Assert.AreEqual(FontStyle.Bold, inventory.Font.Style,
                            "Inventory should read as the active tab while a screen grouped under it is open")
            main.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Nav_tabs_stay_dark_text_on_both_their_resting_and_hover_background()
        Using main As New frmMain() With {.CurrentUserID = 1, .CurrentUserName = "Josiah Obaje", .CurrentUserRole = "Admin"}
            main.Show()
            Application.DoEvents()
            Dim tabs = DirectCast(Field(main, "_tabButtons"), List(Of Button))
            Assert.IsTrue(tabs.Count > 0)
            For Each btn In tabs
                Dim fg = btn.ForeColor
                Dim hoverBg = btn.FlatAppearance.MouseOverBackColor
                Dim textLuma = 0.299 * fg.R + 0.587 * fg.G + 0.114 * fg.B
                Dim hoverLuma = 0.299 * hoverBg.R + 0.587 * hoverBg.G + 0.114 * hoverBg.B
                Assert.IsTrue(textLuma < 140, $"tab text for '{btn.Text}' is too light to read (luma {textLuma:0})")
                Assert.IsTrue(Math.Abs(textLuma - hoverLuma) > 60,
                              $"hover background for '{btn.Text}' is too close to the text colour to read on hover")
            Next
            main.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub All_admin_tabs_fit_the_nav_bar_without_being_clipped()
        Using main As New frmMain() With {.CurrentUserID = 1, .CurrentUserName = "Josiah Obaje", .CurrentUserRole = "Admin"}
            main.Width = 1280
            main.Show()
            Application.DoEvents()
            Dim tabStrip = DirectCast(Field(main, "tabStrip"), FlowLayoutPanel)
            Dim lastBottom = 0
            For Each wrap As Control In tabStrip.Controls
                lastBottom = Math.Max(lastBottom, wrap.Bottom)
            Next
            Assert.IsTrue(lastBottom <= tabStrip.ClientSize.Height,
                         $"a tab (bottom={lastBottom}) is clipped by the nav card (height={tabStrip.ClientSize.Height})")
            main.Close()
        End Using
    End Sub

    ' ===== sales =====

    <TestMethod>
    Public Sub Sale_screen_starts_with_todays_date_and_vat_switched_off()
        Using f As New frmNewInvoice(1)
            f.Show()
            Application.DoEvents()
            Assert.AreEqual(Date.Today, DirectCast(Field(f, "dtpSaleDate"), DateTimePicker).Value.Date)
            Assert.IsFalse(DirectCast(Field(f, "chkVat"), CheckBox).Checked, "VAT is only charged when ticked")
            f.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub A_sale_saved_with_a_past_date_carries_that_date_everywhere()
        Dim customer = TestDb.AddCustomer("Screen Sale Customer")
        Dim product = TestDb.AddProduct("Screen sale feed", 500D)
        Dim warehouse = TestDb.Count("SELECT MIN(WarehouseID) FROM Warehouses")
        TestDb.Exec("INSERT INTO StockBatches (ProductID, WarehouseID, BatchNumber, QuantityOnHand) VALUES (@p, @w, 'SEED', 100)",
                    TestDb.P("@p", product, "@w", warehouse))
        Dim soldOn = Date.Today.AddDays(-21)

        Using f As New frmNewInvoice(1)
            f.Show()
            Application.DoEvents()
            DirectCast(Field(f, "cboCustomer"), ComboBox).SelectedValue = customer
            DirectCast(Field(f, "cboProduct"), ComboBox).SelectedValue = product
            DirectCast(Field(f, "numQty"), NumericUpDown).Value = 2D
            DirectCast(Field(f, "dtpSaleDate"), DateTimePicker).Value = soldOn
            Invoke(f, "btnAddLine_Click", Nothing, EventArgs.Empty)
            Invoke(f, "btnSave_Click", Nothing, EventArgs.Empty)
        End Using

        Dim invoice = TestDb.Table(
            "SELECT TOP 1 InvoiceID, InvoiceNumber, InvoiceDate FROM Invoices WHERE CustomerID=@c ORDER BY InvoiceID DESC",
            TestDb.P("@c", customer))
        Assert.AreEqual(1, invoice.Rows.Count, "the sale should have been saved")
        Dim id = Convert.ToInt32(invoice.Rows(0)("InvoiceID"))
        Assert.AreEqual(soldOn, Convert.ToDateTime(invoice.Rows(0)("InvoiceDate")).Date, "the sale keeps the date entered")
        Assert.IsTrue(Convert.ToString(invoice.Rows(0)("InvoiceNumber")).StartsWith("ChewyStock-"),
                      "the invoice number carries the ChewyStock brand")
        Assert.AreEqual(soldOn,
                        Convert.ToDateTime(TestDb.Scalar("SELECT TOP 1 PaymentDate FROM Payments WHERE InvoiceID=@i", TestDb.P("@i", id))).Date,
                        "the payment is dated with the sale")
        Assert.AreEqual(soldOn,
                        Convert.ToDateTime(TestDb.Scalar("SELECT TOP 1 MovementDate FROM StockMovements WHERE ReferenceType='Invoice' AND ReferenceID=@i", TestDb.P("@i", id))).Date,
                        "the stock movement is dated with the sale")
        Assert.AreEqual(0D, Convert.ToDecimal(TestDb.Scalar("SELECT VATAmount FROM Invoices WHERE InvoiceID=@i", TestDb.P("@i", id))),
                        "no VAT unless it was ticked")
    End Sub

    ' ===== finance / employees =====

    <TestMethod>
    Public Sub Finance_offers_deletion_and_exports_of_every_record()
        Using host As New Form()
            Dim uc As New ucFinance(1)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            Assert.IsTrue(HasText(host, "Delete entry"), "ledger lines must be deletable")
            Assert.IsTrue(HasText(host, "Rows per page"), "long lists are paged")

            ' The exports are one menu now instead of a row of buttons.
            Dim menu = DirectCast(Field(uc, "exportMenu"), ContextMenuStrip)
            Dim labels = menu.Items.Cast(Of ToolStripItem)().Select(Function(i) i.Text).ToList()
            Assert.IsTrue(labels.Any(Function(t) t.IndexOf("CSV", StringComparison.OrdinalIgnoreCase) >= 0), "CSV export: " & String.Join(" | ", labels))
            Assert.IsTrue(labels.Any(Function(t) t.IndexOf("Excel", StringComparison.OrdinalIgnoreCase) >= 0), "Excel export: " & String.Join(" | ", labels))
            Assert.IsTrue(labels.Any(Function(t) t.IndexOf("All finance", StringComparison.OrdinalIgnoreCase) >= 0), "combined export: " & String.Join(" | ", labels))
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Finance_leaves_expenses_to_the_expenses_tab()
        Using host As New Form()
            Dim uc As New ucFinance(1)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            ' Two cramped tables on one screen was the old design, and it
            ' duplicated the Expenses tab. Finance is the ledger screen now.
            Assert.IsFalse(HasText(host, "Operating expenses"),
                           "expenses have their own tab — Finance must not carry a second copy")
            Assert.IsTrue(HasText(host, "Credit and debit ledger"), "the ledger is this screen's table")
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub Employees_screen_offers_the_deletions_needed_to_clear_test_data()
        Using host As New Form()
            Dim uc As New ucEmployees(1)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            Assert.IsTrue(HasText(host, "Delete employee"))
            Assert.IsTrue(HasText(host, "Delete loan"))
            Assert.IsTrue(HasText(host, "Remove from month"))
            Assert.IsTrue(HasText(host, "Attendance"), "admin needs a tab to see clerk check-in/check-out")
            Assert.IsTrue(HasText(host, "Mark this row paid"), "paying one employee at a time is the normal action")
            Assert.IsTrue(HasText(host, "Mark month paid"))
            Assert.IsTrue(HasText(host, "Payroll history"), "a payment must be traceable beyond just this month")
            host.Close()
        End Using
    End Sub

    ''' Selecting an unpaid payroll row and clicking "Mark this row paid" pays
    ''' just that employee, through the real screen wiring (not the module directly).
    <TestMethod>
    Public Sub Selecting_a_payroll_row_and_marking_it_paid_pays_only_that_employee()
        Dim employeeId = TestDb.AddEmployee("Screen Payroll Test " & Guid.NewGuid().ToString("N").Substring(0, 6))
        Dim year = DateTime.Today.Year : Dim month = DateTime.Today.Month
        Dim empMonthId = DataAccess.ExecuteScalarInsert(
            "INSERT INTO EmployeeMonthly (EmployeeID, PeriodYear, PeriodMonth, Position, SalaryAmount) VALUES (@e, @y, @m, 'Clerk', 70000)",
            TestDb.P("@e", employeeId, "@y", year, "@m", month))

        Using host As New Form()
            Dim uc As New ucEmployees(1)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()

            Dim grid = DirectCast(Field(uc, "gridPay"), PagedGrid).Grid
            Dim markBtn = DirectCast(Field(uc, "btnMarkOnePaid"), Button)
            Dim targetRow = grid.Rows.Cast(Of DataGridViewRow)().First(Function(r) Convert.ToInt32(r.Cells("EmpMonthID").Value) = empMonthId)
            grid.ClearSelection()
            targetRow.Selected = True
            Application.DoEvents()
            Assert.IsTrue(markBtn.Enabled, "an unpaid row must enable the button")

            Invoke(uc, "MarkOnePaid_Click", Nothing, EventArgs.Empty)
            Application.DoEvents()

            Assert.IsTrue(Convert.ToBoolean(TestDb.Scalar("SELECT Paid FROM EmployeeMonthly WHERE EmpMonthID=@id", TestDb.P("@id", empMonthId))))
            host.Close()
        End Using
    End Sub

    ''' The admin Attendance tab shows a check-in she just recorded.
    <TestMethod>
    Public Sub Attendance_tab_shows_a_recorded_check_in()
        Dim userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
        TestDb.Exec("DELETE FROM AttendanceEvents WHERE UserID=@u AND WorkDate=@d", TestDb.P("@u", userId, "@d", Date.Today))
        Try
            Attendance.CheckIn(userId, "Attendance Screen Test")
            Using host As New Form()
                Dim uc As New ucEmployees(1)
                uc.Dock = DockStyle.Fill
                host.Controls.Add(uc)
                host.Show()
                Application.DoEvents()
                ' Grid cell values aren't part of the WinForms control tree HasText
                ' walks (they're DataGridView-owned, not child Controls), so check
                ' the bound DataTable directly instead.
                Dim grid = DirectCast(Field(uc, "gridAttendance"), PagedGrid).Grid
                Dim data = DirectCast(grid.DataSource, DataTable)
                Assert.IsTrue(data.AsEnumerable().Any(Function(r) Convert.ToString(r("FullName")) = "Attendance Screen Test"),
                             "the check-in should show up for admin")
                host.Close()
            End Using
        Finally
            TestDb.Exec("DELETE FROM AttendanceEvents WHERE UserID=@u AND WorkDate=@d", TestDb.P("@u", userId, "@d", Date.Today))
        End Try
    End Sub

    <TestMethod>
    Public Sub The_admin_dashboard_carries_the_reorder_table_and_its_advice()
        Using host As New Form()
            Dim uc As New ucDashboard(1, True)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()

            ' Insights used to be a tab you had to remember to visit.
            Dim forecast = DirectCast(Field(uc, "pgForecast"), PagedGrid)
            Assert.IsTrue(Descendants(uc).Contains(forecast), "the reorder forecast belongs on the Dashboard now")
            Assert.IsNotNull(forecast.Grid.DataSource, "it should arrive already filled in")
            Assert.AreEqual(5, forecast.PageSize, "the dashboard tables page rather than run down the screen")

            Dim advice = DirectCast(Field(uc, "pnlAdvice"), FlowLayoutPanel)
            Assert.IsTrue(advice.Visible, "an admin gets the advice panel")
            Assert.IsTrue(advice.Controls.Count > 0, "the advice panel should always say something, even if it's 'all clear'")
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub A_clerk_gets_low_stock_instead_of_the_admin_forecast_and_advice()
        Using host As New Form()
            Dim uc As New ucDashboard(1, False)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()

            Assert.IsFalse(DirectCast(Field(uc, "pnlAdvice"), FlowLayoutPanel).Visible,
                           "business advice is an admin's job, not a clerk's")
            Assert.IsTrue(Descendants(uc).Contains(DirectCast(Field(uc, "pgLowStock"), PagedGrid)),
                          "a clerk still needs to see what's running low")
            Assert.IsFalse(Descendants(uc).Contains(DirectCast(Field(uc, "pgForecast"), PagedGrid)),
                           "the forecast table is admin-only")
            host.Close()
        End Using
    End Sub

    <TestMethod>
    Public Sub The_dashboard_shows_the_brand_once_leaving_the_header_to_do_it()
        Using host As New Form()
            Dim uc As New ucDashboard(1, True)
            uc.Dock = DockStyle.Fill
            host.Controls.Add(uc)
            host.Show()
            Application.DoEvents()
            ' The window header already carries the logo and the app name; a
            ' second copy on the Dashboard was just eating a strip of screen.
            Assert.IsFalse(Descendants(uc).OfType(Of PictureBox)().Any(),
                           "no second logo on the Dashboard — the window header has it")
            host.Close()
        End Using
    End Sub

    ''' A clerk's own Dashboard shows her today's check-in.
    <TestMethod>
    Public Sub Clerks_dashboard_shows_her_own_check_in()
        Dim userId = TestDb.Count("SELECT MIN(UserID) FROM Users")
        TestDb.Exec("DELETE FROM AttendanceEvents WHERE UserID=@u AND WorkDate=@d", TestDb.P("@u", userId, "@d", Date.Today))
        Try
            Attendance.CheckIn(userId, "Dashboard Test Clerk")
            Using host As New Form()
                Dim uc As New ucDashboard(userId, False)
                uc.Dock = DockStyle.Fill
                host.Controls.Add(uc)
                host.Show()
                Application.DoEvents()
                Assert.IsTrue(HasText(host, "Checked in"), "her Dashboard should show today's check-in")
                host.Close()
            End Using
        Finally
            TestDb.Exec("DELETE FROM AttendanceEvents WHERE UserID=@u AND WorkDate=@d", TestDb.P("@u", userId, "@d", Date.Today))
        End Try
    End Sub

    <TestMethod>
    Public Sub Dialogs_keep_their_save_button_reachable_on_a_short_window()
        Using f As New frmAddItem()
            f.Height = 420
            f.Show()
            Application.DoEvents()
            Dim body = f.Controls.OfType(Of Panel)().FirstOrDefault(Function(p) p.AutoScroll)
            Assert.IsNotNull(body, "the fields should sit in a scrolling area")
            Assert.IsTrue(body.VerticalScroll.Visible, "a short window must scroll rather than hide fields")
            Assert.IsTrue(HasText(f, "Save item"), "Save stays on screen")
            f.Close()
        End Using
    End Sub

End Class
