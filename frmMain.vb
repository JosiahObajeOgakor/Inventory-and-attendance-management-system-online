Imports System.Windows.Forms
Imports System.Drawing
Imports System.Configuration

''' Application shell. Modern two-tier top bar:
'''   • brand strip  — logo + wordmark (left), user pill + Appearance + Sign out (right)
'''   • tab strip    — flat nav tabs with an accent underline on the active screen
''' Content panel below swaps in the ucXxx screen. Nav items are filtered by role.
''' All colours/fonts/table design come from Theme (Appearance screen).
Public Class frmMain
    Inherits Form

    ' Tall enough for the tab strip to wrap to two rows (15 admin tabs at a
    ' typical window width) without the card clipping the second row.
    Private ReadOnly pnlNav As New Panel() With {.Dock = DockStyle.Top, .Height = 184}
    Private ReadOnly brandStrip As New Panel() With {.Dock = DockStyle.Top, .Height = 64}
    ' The tabs sit on a raised card with a soft shadow under it.
    Private ReadOnly navCard As New CardPanel() With {.Dock = DockStyle.Fill, .Padding = New Padding(14, 8, 14, 16)}
    Private ReadOnly tabStrip As New FlowLayoutPanel() With {.Dock = DockStyle.Fill, .WrapContents = True, .Padding = New Padding(8, 6, 8, 4)}
    Private ReadOnly pnlContent As New Panel() With {.Dock = DockStyle.Fill, .AutoScroll = True}
    ' Screens never shrink below this; on smaller windows the content scrolls instead.
    Private Shared ReadOnly ScreenMinSize As New Size(1000, 640)

    ' Brand on the left, commands on the right, a stretchy gap between — laid out
    ' by a table rather than by hand. Hand-positioning fought the docking and let
    ' the logo sit on top of the wordmark once the window got narrow.
    Private ReadOnly brandRow As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .ColumnCount = 3, .RowCount = 1}
    Private ReadOnly brandLeft As New FlowLayoutPanel() With {.AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .WrapContents = False, .Anchor = AnchorStyles.Left, .Padding = New Padding(14, 6, 0, 6)}
    Private ReadOnly brandText As New FlowLayoutPanel() With {.FlowDirection = FlowDirection.TopDown, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .WrapContents = False, .Margin = New Padding(12, 2, 0, 0)}
    Private ReadOnly picLogo As New PictureBox() With {.SizeMode = PictureBoxSizeMode.Zoom, .Size = New Size(46, 46), .Margin = New Padding(0, 2, 0, 0)}
    Private ReadOnly lblBrand As New Label() With {.AutoSize = True, .Tag = "keepfont", .Margin = New Padding(0)}
    Private ReadOnly lblCompany As New Label() With {.AutoSize = True, .Tag = "keepfont", .Margin = New Padding(0, 1, 0, 0)}
    Private ReadOnly lblUser As New Label() With {.AutoSize = True, .Tag = "keepfont"}
    Private ReadOnly btnActivate As New Button() With {.Text = "Activate", .Tag = "primary", .AutoSize = True}
    Private ReadOnly btnAccount As New Button() With {.Text = "Account ▾", .AutoSize = True}
    Private ReadOnly btnSettings As New Button() With {.Text = "Appearance", .AutoSize = True}
    Private ReadOnly btnLogout As New Button() With {.Text = "Sign out", .Tag = "danger", .AutoSize = True}
    Private ReadOnly rightGroup As New FlowLayoutPanel() With {.AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .WrapContents = False, .FlowDirection = FlowDirection.LeftToRight, .Anchor = AnchorStyles.Right, .Padding = New Padding(0)}
    Private ReadOnly accountMenu As New ContextMenuStrip()

    Public Property CurrentUserID As Integer
    Public Property CurrentUserName As String
    Public Property CurrentUserRole As String ' "Admin" or "Warehouse Clerk"

    Private ReadOnly Property IsAdminSession As Boolean
        Get
            Return CurrentUserRole = "Admin"
        End Get
    End Property

    Private _currentScreen As String = "Dashboard"
    Private ReadOnly _tabButtons As New List(Of Button)()
    ''' Every screen a tab stands for — the tab itself plus anything grouped
    ''' under it, so a tab still reads as "active" while you're on one of its
    ''' grouped screens.
    Private ReadOnly _tabScreens As New Dictionary(Of Button, String())()
    ' A grouped tab opens its menu on hover. Nothing tells a dropdown "the
    ' pointer has wandered off", so one timer watches where the mouse actually
    ' is and closes the menu once it's over neither the tab nor the menu.
    Private ReadOnly _hoverWatch As New Timer() With {.Interval = 250}
    Private _openMenu As ContextMenuStrip
    Private _openMenuOwner As Button
    Private _idle As IdleWatcher
    Private _idleLoggingOut As Boolean
    ' One accent bar that glides under whichever tab is active.
    Private ReadOnly _tabSlider As New Panel() With {.Height = 3, .Visible = False}
    Private _sliderTween As Timer

    Private _activated As Boolean
    Private ReadOnly _sessionCap As New Timer() With {.Interval = 30000}  ' unactivated: hard logout after 10 min
    Private _sessionStart As DateTime

    Public Sub New()
        Text = Theme.AppName
        WindowState = FormWindowState.Maximized
        Width = 1280
        Height = 800

        brandText.Controls.Add(lblBrand)
        brandText.Controls.Add(lblCompany)
        brandLeft.Controls.Add(picLogo)
        brandLeft.Controls.Add(brandText)
        rightGroup.Controls.Add(lblUser)
        rightGroup.Controls.Add(btnActivate)
        rightGroup.Controls.Add(btnAccount)
        rightGroup.Controls.Add(btnSettings)
        rightGroup.Controls.Add(btnLogout)

        brandRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        brandRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        brandRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        brandRow.Controls.Add(brandLeft, 0, 0)
        brandRow.Controls.Add(New Panel() With {.Dock = DockStyle.Fill, .Margin = New Padding(0)}, 1, 0)
        brandRow.Controls.Add(rightGroup, 2, 0)
        brandStrip.Controls.Add(brandRow)

        navCard.Controls.Add(tabStrip)
        navCard.Controls.Add(_tabSlider)
        _tabSlider.BringToFront()
        pnlNav.Controls.Add(navCard)
        pnlNav.Controls.Add(brandStrip)

        Controls.Add(pnlContent)
        Controls.Add(pnlNav)

        AddHandler brandStrip.Resize, Sub(s, e) LayoutBrandStrip()
        AddHandler pnlContent.ClientSizeChanged, Sub(s, e) FitScreenToContent()
        AddHandler btnLogout.Click, AddressOf btnLogout_Click
        AddHandler btnSettings.Click, AddressOf btnSettings_Click
        AddHandler btnActivate.Click, Sub(s, e) OpenActivation()
        AddHandler btnAccount.Click, Sub(s, e) accountMenu.Show(btnAccount, New Point(0, btnAccount.Height))
        AddHandler _hoverWatch.Tick, AddressOf CloseHoverMenuIfPointerAway
        AddHandler Theme.ThemeChanged, Sub(s, e) ApplyTheme()
        AddHandler Lang.LanguageChanged, Sub(s, e)
                                             BuildAccountMenu()
                                             BuildTabs()
                                             ApplyTheme()
                                         End Sub
        AddHandler _sessionCap.Tick, AddressOf SessionCapTick
        AddHandler Me.Load, Sub(s, e)
                                 lblUser.Text = CurrentUserName & "   ·   " & CurrentUserRole
                                 BeginSession()
                             End Sub
        AddHandler Me.FormClosed, Sub(s, e)
                                      _idle?.Dispose()
                                      _sessionCap.Stop()
                                      ' Closing the app (not just signing out) still logs a check-out time.
                                      Attendance.CheckOut(CurrentUserID)
                                  End Sub
    End Sub

    ''' (Re)initialise everything for the current user + licence state.
    Private Sub BeginSession()
        _activated = Licensing.IsActivated()
        _sessionStart = DateTime.UtcNow
        _sessionCap.Stop()
        BuildAccountMenu()
        BuildTabs()
        If _activated Then
            ShowScreen("Dashboard")
        Else
            ShowLockScreen()
            _sessionCap.Start()   ' 10-minute session cap until activated
        End If
        ApplyTheme()
        StartIdleWatch()
        If _activated Then BeginInvoke(Sub() DbMaintenance.CheckOnLogin(Me, IsAdminSession))
    End Sub

    Private Sub OpenActivation()
        Using f As New frmActivation()
            f.ShowDialog(Me)
        End Using
        If Licensing.IsActivated() AndAlso Not _activated Then
            _activated = True
            _sessionCap.Stop()
            AppUI.Toast("Activated — everything is unlocked.", AppUI.ToastKind.Success, Me)
            BuildAccountMenu()
            BuildTabs()
            ShowScreen("Dashboard")
            ApplyTheme()
        End If
    End Sub

    Private Sub SessionCapTick(sender As Object, e As EventArgs)
        If _activated Then
            _sessionCap.Stop()
            Return
        End If
        Dim mins = (DateTime.UtcNow - _sessionStart).TotalMinutes
        If mins >= 9 AndAlso mins < 9.6 Then
            AppUI.Toast("Activate to keep working — this session ends in about a minute.", AppUI.ToastKind.Warning, Me)
        ElseIf mins >= 10 Then
            _sessionCap.Stop()
            AppUI.Toast("Session ended — activate ChewyStock to use it without interruption.", AppUI.ToastKind.Info, Me)
            btnLogout_Click(Nothing, EventArgs.Empty)
        End If
    End Sub

    ''' Full-panel lock screen shown to unactivated sessions instead of any module.
    Private Sub ShowLockScreen()
        pnlContent.Controls.Clear()
        _currentScreen = "Locked"

        Dim host As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .ColumnCount = 1, .Padding = New Padding(60, 48, 60, 48)}
        host.Controls.Add(New Label() With {.Text = "ChewyStock is not activated", .AutoSize = True, .Tag = "keepfont",
            .Font = Theme.HeadingFont(1.6F), .ForeColor = Theme.Current.TextPrimary, .Margin = New Padding(0, 0, 0, 12)})
        host.Controls.Add(New Label() With {.AutoSize = True, .Tag = "keepfont", .MaximumSize = New Size(680, 0),
            .ForeColor = Theme.Current.TextMuted, .Margin = New Padding(0, 0, 0, 20),
            .Text = "Every business module — inventory, sales, receipts, customers, income, payroll and the rest — is locked until this installation is activated." & vbCrLf & vbCrLf &
                    "Click ""Activate now"" to buy a $250 lifetime licence. After payment the app activates itself for this computer automatically — permanently, and offline from then on." & vbCrLf & vbCrLf &
                    "You can create your own administrator account now, but you can't add staff accounts until activated, and each session ends after 10 minutes."})

        Dim btn As New Button() With {.Text = "Activate now", .Tag = "primary", .AutoSize = True}
        Theme.StylePrimaryButton(btn)
        AddHandler btn.Click, Sub(s, e) OpenActivation()
        host.Controls.Add(btn)

        host.Controls.Add(New Label() With {.Text = "Machine ID: " & Licensing.MachineId, .AutoSize = True, .Tag = "keepfont",
            .ForeColor = Theme.Current.TextMuted, .Margin = New Padding(0, 24, 0, 0), .Font = New Font("Consolas", 10)})

        pnlContent.Controls.Add(host)
        RestyleTabs()
    End Sub

    Private Sub BuildAccountMenu()
        accountMenu.Items.Clear()
        accountMenu.Items.Add("Change my password…", Nothing, Sub(s, e)
                                                                   Using f As New frmSetPassword(CurrentUserID, requireOld:=True, title:="Change my password")
                                                                       f.ShowDialog(Me)
                                                                   End Using
                                                               End Sub)
        If IsAdminSession Then
            Dim addItem = accountMenu.Items.Add("Add user account…", Nothing, Sub(s, e)
                                                                                  If Not _activated Then
                                                                                      AppUI.Info(Me, "Activate ChewyStock to add staff accounts. You can use your own admin account in the meantime.")
                                                                                      Return
                                                                                  End If
                                                                                  Using f As New frmRegister()
                                                                                      f.ShowDialog(Me)
                                                                                  End Using
                                                                              End Sub)
            addItem.Enabled = _activated
            accountMenu.Items.Add("Backup database…", Nothing, Sub(s, e) BackupDatabase())
            accountMenu.Items.Add("Export all data (Excel)…", Nothing, Sub(s, e) ExportAllData())
            accountMenu.Items.Add("Database storage and archive…", Nothing, Sub(s, e)
                                                                                Using f As New frmArchive()
                                                                                    f.ShowDialog(Me)
                                                                                End Using
                                                                            End Sub)
            accountMenu.Items.Add("Installation / license info…", Nothing, Sub(s, e)
                                                                               AppUI.Info(Me,
                                                                                   "Machine ID: " & Licensing.MachineId & vbCrLf &
                                                                                   "Status: " & If(Licensing.IsActivated(), "Activated", "Not activated"))
                                                                           End Sub)
        End If
    End Sub

    ''' Full native SQL Server backup (.bak) — a complete, restorable copy of
    ''' the database. Run this periodically and keep the file somewhere safe
    ''' (a USB drive, cloud folder) — it is the actual disaster-recovery copy.
    Private Sub BackupDatabase()
        Using sfd As New SaveFileDialog() With {
            .Filter = "SQL Server backup (*.bak)|*.bak",
            .FileName = $"ChewyStock_backup_{DateTime.Now:yyyyMMdd_HHmmss}.bak",
            .Title = "Save database backup"}
            If sfd.ShowDialog(Me) <> DialogResult.OK Then Return
            Cursor = Cursors.WaitCursor
            Dim err = DbBootstrap.BackupTo(sfd.FileName)
            Cursor = Cursors.Default
            If err = "" Then
                AppUI.Toast("Backup saved: " & IO.Path.GetFileName(sfd.FileName), AppUI.ToastKind.Success, Me)
            Else
                AppUI.Toast("Backup failed: " & err, AppUI.ToastKind.Error, Me)
            End If
        End Using
    End Sub

    ''' Every business table as one Excel workbook — for spreadsheet review or
    ''' handing data to an accountant, not a substitute for the .bak backup above.
    Private Sub ExportAllData()
        Cursor = Cursors.WaitCursor
        Dim sheets = Exporter.BuildFullExport()
        Cursor = Cursors.Default
        Exporter.SaveExcel(sheets, Company.Current.FilePrefix & "_full_export_" & DateTime.Now.ToString("yyyyMMdd_HHmmss"), Me)
    End Sub

    ''' Admin sessions only: after the configured idle minutes
    ''' (AdminIdleLogoutMinutes, default 5) a modal warns and counts down
    ''' (AdminIdleWarningMinutes, default 3). "Stay signed in" resets the
    ''' watch; otherwise the admin is signed out when it reaches zero.
    ''' Clerks are never idle-logged-out.
    Private Sub StartIdleWatch()
        If Not IsAdminSession Then Return
        Dim mins = 5
        If Not Integer.TryParse(ConfigurationManager.AppSettings("AdminIdleLogoutMinutes"), mins) Then mins = 5
        If mins <= 0 Then Return
        _idle = New IdleWatcher(TimeSpan.FromMinutes(mins))
        AddHandler _idle.WentIdle, Sub(s, e) BeginInvoke(New Action(AddressOf IdleWarn))
        _idle.Start()
    End Sub

    Private Sub IdleWarn()
        If _idleLoggingOut OrElse _idle Is Nothing OrElse Not IsAdminSession Then Return
        Dim warnMins = 3
        If Not Integer.TryParse(ConfigurationManager.AppSettings("AdminIdleWarningMinutes"), warnMins) OrElse warnMins <= 0 Then warnMins = 3

        Dim result As DialogResult
        Using f As New frmIdleWarning(TimeSpan.FromMinutes(warnMins))
            result = f.ShowDialog(If(Form.ActiveForm, CType(Me, IWin32Window)))
        End Using
        If result = DialogResult.OK Then
            _idle?.Start()
        Else
            IdleLogout()
        End If
    End Sub

    Private Sub IdleLogout()
        If _idleLoggingOut Then Return
        _idleLoggingOut = True
        _idle?.Dispose()
        _idle = Nothing
        ' Close any dialog the admin left open so nothing stays on screen
        ' behind the login window; sign out once their modal loops unwind.
        For Each f In Application.OpenForms.Cast(Of Form)().Where(Function(x) x IsNot Me AndAlso Not TypeOf x Is frmLogin).ToList()
            Try
                f.Close()
            Catch
            End Try
        Next
        BeginInvoke(Sub()
                        _idleLoggingOut = False
                        ' Returns only after the next person signs in.
                        btnLogout_Click(Nothing, EventArgs.Empty)
                    End Sub)
    End Sub

    ''' Allocates the strip's finite width before positioning either group. This
    ''' prevents the logo/text and session commands competing for the same pixels
    ''' when names or the configured font size are large.
    ''' The table places everything; this only caps how wide the wordmark may
    ''' grow, so a long company name gives way rather than shoving the buttons
    ''' off the right edge, and keeps the strip tall enough for both sides.
    Private Sub LayoutBrandStrip()
        If brandStrip.ClientSize.Width <= 0 Then Return

        Dim textRoom = Math.Max(120, brandStrip.ClientSize.Width - rightGroup.PreferredSize.Width - picLogo.Width - 90)
        lblBrand.MaximumSize = New Size(textRoom, 0)
        lblCompany.MaximumSize = New Size(textRoom, 0)

        Dim stripHeight = Math.Max(64, Math.Max(brandLeft.PreferredSize.Height, rightGroup.PreferredSize.Height) + 12)
        If brandStrip.Height <> stripHeight Then brandStrip.Height = stripHeight
    End Sub

    ''' Restyle the shell to the current Theme. Called on load + on Appearance change.
    Private Sub ApplyTheme()
        Dim navBg = Theme.Current.NavBg

        pnlNav.BackColor = Theme.Current.WindowBg
        brandStrip.BackColor = navBg

        picLogo.Image = Theme.Logo
        picLogo.Visible = Theme.Logo IsNot Nothing
        lblBrand.Text = Theme.AppName
        lblBrand.ForeColor = Theme.Current.NavFg
        lblBrand.Font = Theme.HeadingFont(1.15F)
        lblCompany.Text = AppInfo.CompanyName
        ' Company name only shown when it differs from the app name — no repeat clutter.
        lblCompany.Visible = Not String.Equals(AppInfo.CompanyName, Theme.AppName, StringComparison.OrdinalIgnoreCase)
        lblCompany.ForeColor = Color.FromArgb(190, Theme.Current.NavFg)
        lblCompany.Font = New Font("Segoe UI", Math.Max(7.5F, Theme.BaseFontSize - 4.5F))

        navCard.CardColor = Theme.Current.Surface
        navCard.Invalidate()
        tabStrip.BackColor = Theme.Current.Surface

        lblUser.ForeColor = Theme.Current.NavFg
        lblUser.Font = Theme.BaseFont()
        lblUser.Margin = New Padding(0, 8, 14, 0)

        Theme.StyleDangerButton(btnLogout)
        btnLogout.Margin = New Padding(0)
        For Each b In {btnAccount, btnSettings}
            b.FlatStyle = FlatStyle.Flat
            b.FlatAppearance.BorderColor = Theme.Current.NavFg
            b.FlatAppearance.BorderSize = 1
            b.BackColor = navBg
            b.ForeColor = Theme.Current.NavFg
            b.Font = Theme.BaseFont()
            b.Padding = New Padding(12, 6, 12, 6)
            b.Margin = New Padding(0, 0, 8, 0)
        Next
        btnActivate.Visible = Not _activated
        If Not _activated Then
            Theme.StylePrimaryButton(btnActivate)
            btnActivate.Margin = New Padding(0, 0, 8, 0)
        End If

        RestyleTabs()
        LayoutBrandStrip()

        BackColor = Theme.Current.WindowBg
        For Each c As Control In pnlContent.Controls
            Theme.Apply(c)
        Next
        Lang.ApplyByText(Me)   ' translate shell captions (tabs, buttons)
        RestyleTabs()          ' widths change with translated labels
    End Sub

    ''' One entry in the nav strip. `Children` are screens grouped under it: they
    ''' get no tab of their own, they're reached from the parent tab's menu.
    Private NotInheritable Class NavItem
        Public ReadOnly Key As String
        Public ReadOnly Label As String
        Public ReadOnly AdminOnly As Boolean
        Public ReadOnly Children As NavItem()

        Public Sub New(key As String, label As String, adminOnly As Boolean, ParamArray children As NavItem())
            Me.Key = key
            Me.Label = label
            Me.AdminOnly = adminOnly
            Me.Children = children
        End Sub
    End Class

    Private Sub BuildTabs()
        tabStrip.Controls.Clear()
        _tabButtons.Clear()
        _tabScreens.Clear()

        Dim isAdmin = CurrentUserRole = "Admin"
        ' Buying (purchases/suppliers) sits under Inventory, and the money
        ' screens (expenses/income/rebates) under Finance — fifteen tabs across
        ' the top was more than anyone could scan.
        Dim items = {
            New NavItem("Dashboard", "Dashboard", False),
            New NavItem("Inventory", "Inventory", False,
                        New NavItem("Purchases", "Purchases", True),
                        New NavItem("Suppliers", "Suppliers", True)),
            New NavItem("Customers", "Customers", True,
                        New NavItem("Sales", "Sales", False),
                        New NavItem("Quotations", "Quotations", False),
                        New NavItem("Receipts", "Receipts", False),
                        New NavItem("Waybill", "Waybill", False)),
            New NavItem("Finance", "Finance", True,
                        New NavItem("Expenses", "Expenses", True),
                        New NavItem("Income", "Income", True),
                        New NavItem("Rebates", "Rebates", True)),
            New NavItem("Employees", "Employees", True),
            New NavItem("AI", "AI Assistant", True)
        }.ToList()
        ' Candid Purrfect keeps a price book; ChewyPets doesn't, so its tabs stay exactly as they were.
        If Company.Current.HasPriceLists Then items.Insert(2, New NavItem("PriceList", "Price list", False))

        For Each item In items
            Dim children = item.Children.Where(Function(c) isAdmin OrElse Not c.AdminOnly).ToArray()
            ' A clerk can't open Customers, but she still needs Sales, Receipts
            ' and Waybill from under it — so the tab survives on its children,
            ' takes the first one's name, and drops the entry she can't use.
            Dim ownVisible = isAdmin OrElse Not item.AdminOnly
            If Not ownVisible AndAlso children.Length = 0 Then Continue For
            Dim tabKey = If(ownVisible, item.Key, children(0).Key)
            Dim tabLabel = If(ownVisible, item.Label, children(0).Label)

            Dim wrap As New Panel() With {.Height = 40, .Margin = New Padding(0)}
            Dim underline As New Panel() With {.Dock = DockStyle.Bottom, .Height = 3}
            Dim btn As New Button() With {.Dock = DockStyle.Fill, .Tag = tabKey, .Enabled = _activated,
                                          .Text = If(children.Length > 0, tabLabel & " ▾", tabLabel)}
            btn.FlatStyle = FlatStyle.Flat
            btn.FlatAppearance.BorderSize = 0

            ' What's grouped under a tab drops down as soon as you point at it —
            ' no click needed to find out it's there. Clicking still opens the
            ' tab's own screen, and the menu lists that screen first so it's
            ' reachable either way.
            Dim menu As ContextMenuStrip = Nothing
            If children.Length > 0 Then
                menu = New ContextMenuStrip()
                If ownVisible Then
                    menu.Items.Add(item.Label, Nothing, Sub(s, e) ShowScreen(item.Key))
                    menu.Items.Add(New ToolStripSeparator())
                End If
                For Each child In children
                    Dim childKey = child.Key
                    menu.Items.Add(child.Label, Nothing, Sub(s, e) ShowScreen(childKey))
                Next
            End If
            Dim ownMenu = menu
            AddHandler btn.Click, Sub(s, e) ShowScreen(CStr(CType(s, Button).Tag))
            If ownMenu IsNot Nothing Then
                AddHandler btn.MouseEnter, Sub(s, e) OpenHoverMenu(btn, ownMenu)
            End If

            wrap.Controls.Add(btn)
            wrap.Controls.Add(underline)
            wrap.Tag = underline
            wrap.Width = TextRenderer.MeasureText(btn.Text, Theme.BaseFont()).Width + 34
            ' Tabs re-wrap when the window resizes; keep the slider under the active one.
            AddHandler wrap.LocationChanged, Sub(s, e) MoveTabSlider(animate:=False)
            AddHandler wrap.SizeChanged, Sub(s, e) MoveTabSlider(animate:=False)
            tabStrip.Controls.Add(wrap)
            _tabButtons.Add(btn)
            _tabScreens(btn) = {item.Key}.Concat(children.Select(Function(c) c.Key)).ToArray()
        Next
        RestyleTabs()
    End Sub

    ''' Drops a grouped tab's menu open under it. Any menu already open belongs
    ''' to a different tab, so it goes first — sliding along the strip shouldn't
    ''' leave a trail of open menus.
    Private Sub OpenHoverMenu(btn As Button, menu As ContextMenuStrip)
        If Not _activated Then Return
        If _openMenu IsNot Nothing AndAlso _openMenu IsNot menu Then _openMenu.Close()
        _openMenu = menu
        _openMenuOwner = btn
        If Not menu.Visible Then menu.Show(btn, New Point(0, btn.Height))
        _hoverWatch.Start()
    End Sub

    ''' Closes the open menu once the pointer is over neither it nor its tab.
    Private Sub CloseHoverMenuIfPointerAway(sender As Object, e As EventArgs)
        If _openMenu Is Nothing OrElse Not _openMenu.Visible Then
            _hoverWatch.Stop()
            Return
        End If
        Dim onTab = _openMenuOwner IsNot Nothing AndAlso Not _openMenuOwner.IsDisposed AndAlso
                    _openMenuOwner.ClientRectangle.Contains(_openMenuOwner.PointToClient(Cursor.Position))
        If onTab OrElse _openMenu.Bounds.Contains(Cursor.Position) Then Return

        _openMenu.Close()
        _openMenu = Nothing
        _openMenuOwner = Nothing
        _hoverWatch.Stop()
    End Sub

    ''' Tabs sit on a plain white/surface card now (not the dark nav colour), so
    ''' both the resting and hover text stay dark and readable — never white-on-
    ''' near-white the way a lightened brand colour could end up.
    Private Sub RestyleTabs()
        Dim surface = Theme.Current.Surface
        Dim hoverBg = Theme.Current.GridSelectionBg
        For Each btn In _tabButtons
            Dim screens As String() = Nothing
            Dim active = If(_tabScreens.TryGetValue(btn, screens), screens.Contains(_currentScreen),
                            CStr(btn.Tag) = _currentScreen)
            Dim wrap = btn.Parent
            Dim underline = TryCast(wrap.Tag, Panel)

            btn.BackColor = surface
            btn.ForeColor = If(active, Theme.Current.Primary, Theme.Current.TextPrimary)
            btn.FlatAppearance.MouseOverBackColor = hoverBg
            btn.Font = New Font("Segoe UI", Theme.BaseFontSize, If(active, FontStyle.Bold, FontStyle.Regular))
            ' The shared slider draws the active underline (static colour if animations are off).
            If underline IsNot Nothing Then underline.BackColor = If(active AndAlso Not Anim.Enabled, Theme.Current.Primary, surface)
            wrap.Width = TextRenderer.MeasureText(btn.Text, btn.Font).Width + 34
        Next
        _tabSlider.BackColor = Theme.Current.Primary
        MoveTabSlider(animate:=True)
    End Sub

    ''' Glides the accent bar from the previous tab to the active one.
    Private Sub MoveTabSlider(animate As Boolean)
        If Not Anim.Enabled Then
            _tabSlider.Visible = False
            Return
        End If
        Dim activeBtn = _tabButtons.FirstOrDefault(
            Function(b)
                Dim screens As String() = Nothing
                Return If(_tabScreens.TryGetValue(b, screens), screens.Contains(_currentScreen), CStr(b.Tag) = _currentScreen)
            End Function)
        If activeBtn Is Nothing OrElse activeBtn.Parent Is Nothing Then
            _tabSlider.Visible = False
            Return
        End If
        Dim wrap = activeBtn.Parent
        Dim targetX = tabStrip.Left + wrap.Left + 6
        Dim targetY = tabStrip.Top + wrap.Bottom - _tabSlider.Height
        Dim targetW = Math.Max(10, wrap.Width - 12)

        _sliderTween?.Stop()
        If Not animate OrElse Not _tabSlider.Visible Then
            _tabSlider.SetBounds(targetX, targetY, targetW, _tabSlider.Height)
            _tabSlider.Visible = True
            _tabSlider.BringToFront()
            Return
        End If
        Dim fromX = _tabSlider.Left, fromW = _tabSlider.Width
        _tabSlider.Top = targetY
        _sliderTween = Anim.Tween(320, Sub(t)
                                           _tabSlider.Left = CInt(Anim.Lerp(fromX, targetX, t))
                                           _tabSlider.Width = CInt(Anim.Lerp(fromW, targetW, t))
                                       End Sub, ease:=AddressOf Anim.EaseOutBack)
    End Sub

    ''' Swaps the active UserControl into the content panel.
    Private Sub ShowScreen(key As String)
        If Not _activated Then
            ShowLockScreen()
            Return
        End If
        pnlContent.Controls.Clear()
        _currentScreen = key
        Dim isAdmin = CurrentUserRole = "Admin"
        Dim uc As UserControl
        Select Case key
            Case "Dashboard" : uc = New ucDashboard(CurrentUserID, isAdmin)
            Case "Inventory" : uc = New ucInventory(CurrentUserID, isAdmin)
            Case "Sales", "Receipts" : uc = New ucInvoices(CurrentUserID, isAdmin)
            Case "Quotations" : uc = New ucQuotations(CurrentUserID, isAdmin)
            Case "Suppliers" : uc = New ucSuppliers(CurrentUserID)
            Case "Purchases" : uc = New ucSuppliers(CurrentUserID, purchaseOrdersOnly:=True)
            Case "Customers" : uc = New ucCustomers(CurrentUserID)
            Case "Rebates" : uc = New ucRebates()
            Case "Waybill" : uc = New ucWaybill(CurrentUserID)
            Case "Income" : uc = New ucIncome()
            Case "Finance" : uc = New ucFinance(CurrentUserID)
            Case "Expenses" : uc = New ucExpenses(CurrentUserID)
            Case "Employees" : uc = New ucEmployees(CurrentUserID)
            Case "AI" : uc = New ucAIAssistant(CurrentUserRole)
            Case "PriceList" : uc = New ucPriceList(CurrentUserID, isAdmin)
            Case Else : Return
        End Select
        pnlContent.AutoScrollPosition = Point.Empty
        uc.Location = Point.Empty
        pnlContent.Controls.Add(uc)
        FitScreenToContent()
        Theme.Apply(uc)
        RestyleTabs()
        Anim.FadeInOver(pnlContent, uc)
    End Sub

    ''' Screen fills the window, but never below ScreenMinSize — smaller windows
    ''' (laptops, 125–150% display scaling) get scrollbars instead of squashed grids.
    Private Sub FitScreenToContent()
        Dim uc = pnlContent.Controls.OfType(Of UserControl)().FirstOrDefault()
        If uc Is Nothing Then Return
        uc.Size = New Size(Math.Max(ScreenMinSize.Width, pnlContent.ClientSize.Width),
                           Math.Max(ScreenMinSize.Height, pnlContent.ClientSize.Height))
    End Sub

    Private Sub btnSettings_Click(sender As Object, e As EventArgs)
        Using f As New frmAppearance()
            f.ShowDialog(Me)
        End Using
    End Sub

    Private Sub btnLogout_Click(sender As Object, e As EventArgs)
        ' Log out the departing session's check-out time before switching users.
        Attendance.CheckOut(CurrentUserID)
        _idle?.Dispose()
        _idle = Nothing
        _sessionCap.Stop()
        Me.Hide()
        Do
            Using login As New frmLogin()
                If login.ShowDialog() <> DialogResult.OK Then
                    Application.Exit()
                    Return
                End If
                CurrentUserID = login.LoggedInUserID
                CurrentUserName = login.LoggedInFullName
                CurrentUserRole = login.LoggedInRole
                lblUser.Text = CurrentUserName & "   ·   " & CurrentUserRole
                ' She can decline and sign out instead of checking in — loop back to login rather than opening the app.
                If frmClerkWelcome.ShowFor(CurrentUserID, CurrentUserRole, CurrentUserName) Then Exit Do
            End Using
        Loop
        BeginSession()
        Me.Show()
    End Sub

End Class
