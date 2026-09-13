Imports System.Data
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

''' Database storage & archive (admins). Shows how full the database is and
''' moves old, settled records out to Excel + CSV files — after a full backup.
Public Class frmArchive
    Inherits Form

    Private ReadOnly lblUsage As New Label() With {.AutoSize = True, .Tag = "keepfont", .Margin = New Padding(0, 4, 0, 4)}
    Private ReadOnly bar As New ProgressBar() With {.Dock = DockStyle.Top, .Height = 14, .Maximum = 1000}
    Private ReadOnly dtp As New DateTimePicker() With {.Format = DateTimePickerFormat.Custom, .CustomFormat = "dd MMM yyyy", .Width = 150}
    Private ReadOnly grid As New DataGridView() With {.Dock = DockStyle.Fill, .ReadOnly = True, .AllowUserToAddRows = False,
        .RowHeadersVisible = False, .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, .SelectionMode = DataGridViewSelectionMode.FullRowSelect}
    Private ReadOnly lblStatus As New Label() With {.AutoSize = True, .Tag = "keepfont", .MaximumSize = New Size(600, 0), .Margin = New Padding(0, 8, 0, 0)}
    Private ReadOnly lblBackups As New Label() With {.AutoSize = True, .Tag = "keepfont", .MaximumSize = New Size(600, 0), .Margin = New Padding(0, 10, 0, 0)}
    Private ReadOnly btnArchive As New Button() With {.Text = "Back up and archive…", .AutoSize = True, .Tag = "danger"}

    Private Shared ReadOnly Friendly As New Dictionary(Of String, String) From {
        {"Invoices", "Paid invoices"}, {"InvoiceItems", "Invoice lines"}, {"Payments", "Payments received"},
        {"Waybills", "Waybills"}, {"RebateEntries", "Redeemed rebates"}, {"PurchaseOrders", "Paid purchase orders"},
        {"PurchaseOrderItems", "Purchase order lines"}, {"Expenses", "Expenses"}, {"Ledger", "Ledger entries"},
        {"StockMovements", "Stock movements"}, {"EmployeeMonthly", "Paid payroll months"},
        {"EmployeeLoans", "Closed staff loans"}, {"LoanRepayments", "Loan repayments"}, {"LoginAudit", "Sign-in history"}}

    Public Sub New()
        Text = Theme.AppName & " — Database storage & archive"
        Width = 680
        Height = 700
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False
        MaximizeBox = False

        Dim root As New TableLayoutPanel() With {.Dock = DockStyle.Fill, .ColumnCount = 1, .Padding = New Padding(24, 18, 24, 8)}
        Dim addRow = Sub(c As Control, style As RowStyle)
                         root.RowStyles.Add(style)
                         root.Controls.Add(c, 0, root.RowStyles.Count - 1)
                     End Sub

        addRow(New Label() With {.Text = "Database storage", .AutoSize = True, .Tag = "heading", .Font = Theme.HeadingFont(1.2F)}, New RowStyle(SizeType.AutoSize))
        addRow(lblUsage, New RowStyle(SizeType.AutoSize))
        addRow(bar, New RowStyle(SizeType.Absolute, 22))
        addRow(New Label() With {.AutoSize = True, .Tag = "keepfont", .MaximumSize = New Size(600, 0), .Margin = New Padding(0, 10, 0, 10),
            .Text = "When the database is full, new sales and records can't be saved. Archiving first saves a full backup (.bak), " &
                    "then copies old, settled records into Excel and CSV files for you to keep, and removes them from ChewyStock."},
            New RowStyle(SizeType.AutoSize))

        Dim cutRow As New FlowLayoutPanel() With {.AutoSize = True, .Dock = DockStyle.Top, .Margin = New Padding(0)}
        cutRow.Controls.Add(New Label() With {.Text = "Archive settled records dated before:", .AutoSize = True, .Margin = New Padding(0, 6, 8, 0)})
        cutRow.Controls.Add(dtp)
        addRow(cutRow, New RowStyle(SizeType.AutoSize))
        addRow(grid, New RowStyle(SizeType.Percent, 100))
        addRow(New Label() With {.AutoSize = True, .Tag = "keepfont", .MaximumSize = New Size(600, 0), .ForeColor = Theme.Current.TextMuted, .Margin = New Padding(0, 6, 0, 0),
            .Text = "Always kept, however old: unpaid or part-paid invoices, unpaid purchase orders, open staff loans, unredeemed rebates, " &
                    "and all products, stock levels, customers (with balances), suppliers and employees."},
            New RowStyle(SizeType.AutoSize))
        addRow(lblBackups, New RowStyle(SizeType.AutoSize))
        addRow(lblStatus, New RowStyle(SizeType.AutoSize))

        Dim buttons As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(16)}
        Dim btnClose As New Button() With {.Text = "Close", .AutoSize = True, .DialogResult = DialogResult.Cancel}
        Dim btnBackup As New Button() With {.Text = "Back up only…", .AutoSize = True, .Tag = "primary"}
        Theme.StyleDangerButton(btnArchive)
        Theme.StylePrimaryButton(btnBackup)
        buttons.Controls.Add(btnClose)
        buttons.Controls.Add(btnArchive)
        buttons.Controls.Add(btnBackup)
        CancelButton = btnClose

        Controls.Add(root)
        Controls.Add(buttons)

        dtp.MaxDate = Date.Today
        dtp.Value = New Date(Date.Today.Year - 1, 1, 1)   ' keeps this year + last year
        AddHandler dtp.ValueChanged, Sub(s, e) RefreshPreview()
        AddHandler btnArchive.Click, AddressOf Archive_Click
        AddHandler btnBackup.Click, AddressOf Backup_Click
        AddHandler Load, Sub(s, e)
                             RefreshUsage()
                             RefreshPreview()
                             ShowBackupState()
                         End Sub

        Theme.Apply(Me)
        Theme.ApplyGrid(grid)
    End Sub

    Private Sub RefreshUsage()
        Try
            Dim u = DbMaintenance.GetUsage()
            If u.LimitMB > 0 Then
                lblUsage.Text = $"Using {u.UsedMB:#,0.0} MB of {u.LimitMB:#,0} MB ({u.Percent:0.0}% full)"
                bar.Value = CInt(Math.Min(1000, Math.Max(0, u.Percent * 10)))
            Else
                lblUsage.Text = $"Using {u.UsedMB:#,0.0} MB (no size limit on this database server)"
                bar.Visible = False
            End If
        Catch ex As Exception
            lblUsage.Text = "Couldn't read the database size: " & ex.Message
        End Try
    End Sub

    ''' Automatic backups happen on the first sign-in each day; this says where
    ''' they are and reminds people to copy them off the PC.
    Private Sub ShowBackupState()
        Dim latest = BackupService.LatestBackup()
        lblBackups.ForeColor = Theme.Current.TextMuted
        If latest Is Nothing Then
            lblBackups.Text = "Automatic backups: none taken yet — one is written the first time ChewyStock is used each day." & vbCrLf &
                              "Folder: " & BackupService.BackupFolder()
        Else
            lblBackups.Text = $"Automatic backups: last one {latest.LastWriteTime:dd MMM yyyy HH:mm} ({latest.Length / 1024 / 1024.0:0.0} MB), " &
                              $"keeping the newest {BackupService.KeepCount}." & vbCrLf &
                              "Folder: " & BackupService.BackupFolder() & vbCrLf &
                              "These sit on this computer — copy the folder to a USB drive or cloud storage now and then."
        End If
    End Sub

    Private _total As Integer

    Private Sub RefreshPreview()
        Try
            Dim t As New DataTable()
            t.Columns.Add("Records")
            t.Columns.Add("To archive", GetType(Integer))
            _total = 0
            For Each p In DbMaintenance.Preview(dtp.Value.Date)
                t.Rows.Add(If(Friendly.ContainsKey(p.Table), Friendly(p.Table), p.Table), p.Rows)
                _total += p.Rows
            Next
            grid.DataSource = t
            btnArchive.Enabled = _total > 0
            lblStatus.ForeColor = Theme.Current.TextMuted
            lblStatus.Text = If(_total = 0, $"Nothing to archive before {dtp.Value:dd MMM yyyy}.",
                                            $"{_total:#,0} records would be archived.")
        Catch ex As Exception
            btnArchive.Enabled = False
            lblStatus.ForeColor = Color.Firebrick
            lblStatus.Text = "Couldn't count records: " & ex.Message
        End Try
    End Sub

    Private Function PickFolder(description As String) As String
        Using fbd As New FolderBrowserDialog() With {.Description = description, .ShowNewFolderButton = True}
            Return If(fbd.ShowDialog(Me) = DialogResult.OK, fbd.SelectedPath, Nothing)
        End Using
    End Function

    Private Sub Backup_Click(sender As Object, e As EventArgs)
        Using sfd As New SaveFileDialog() With {
            .Filter = "SQL Server backup (*.bak)|*.bak",
            .FileName = $"ChewyStock_backup_{DateTime.Now:yyyyMMdd_HHmmss}.bak",
            .Title = "Save database backup"}
            If sfd.ShowDialog(Me) <> DialogResult.OK Then Return
            Cursor = Cursors.WaitCursor
            Dim err = DbBootstrap.BackupTo(sfd.FileName)
            Cursor = Cursors.Default
            If err = "" Then
                lblStatus.ForeColor = Theme.Current.TextPrimary
                lblStatus.Text = "Backup saved: " & sfd.FileName
                ShowBackupState()
            Else
                lblStatus.ForeColor = Color.Firebrick
                lblStatus.Text = "Backup failed: " & err
            End If
        End Using
    End Sub

    Private Sub Archive_Click(sender As Object, e As EventArgs)
        Dim cut = dtp.Value.Date
        RefreshPreview()
        If _total = 0 Then Return

        Dim parent = PickFolder("Choose where to save the backup and archive files. A USB drive or a cloud-synced folder (OneDrive, Google Drive) is best.")
        If parent Is Nothing Then Return
        Dim folder = Path.Combine(parent, $"ChewyStock_Archive_before_{cut:yyyy-MM-dd}_{DateTime.Now:yyyyMMdd_HHmmss}")

        If Not AppUI.Confirm(Me,
            $"Archive {_total:#,0} records dated before {cut:dd MMM yyyy}?" & vbCrLf & vbCrLf &
            "A full backup is saved first. The records are then exported to Excel + CSV and removed from ChewyStock.",
            "Archive old records", "Back up and archive", danger:=True) Then Return

        Cursor = Cursors.WaitCursor
        btnArchive.Enabled = False
        lblStatus.ForeColor = Theme.Current.TextPrimary
        lblStatus.Text = "Saving full backup…"
        Application.DoEvents()
        Try
            Directory.CreateDirectory(folder)
            Dim err = DbBootstrap.BackupTo(Path.Combine(folder, "ChewyStock_full_backup.bak"))
            If err <> "" Then
                lblStatus.ForeColor = Color.Firebrick
                lblStatus.Text = "Backup failed, so nothing was archived: " & err
                Return
            End If

            lblStatus.Text = "Exporting and archiving records…"
            Application.DoEvents()
            Dim n = DbMaintenance.Archive(cut, folder)

            RefreshUsage()
            RefreshPreview()
            lblStatus.ForeColor = Theme.Current.TextPrimary
            lblStatus.Text = $"Archived {n:#,0} records into:" & vbCrLf & folder & vbCrLf & vbCrLf &
                             "• ChewyStock_full_backup.bak — complete backup taken before archiving" & vbCrLf &
                             "• Archive.xlsx — the archived records, one sheet per table" & vbCrLf &
                             "• csv\ — the same records as CSV files" & vbCrLf &
                             "Keep this folder safe (USB drive or cloud storage)."
            AppUI.Toast($"Archived {n:#,0} records.", AppUI.ToastKind.Success, Me)
            Try
                Process.Start("explorer.exe", """" & folder & """")
            Catch
            End Try
        Catch ex As Exception
            lblStatus.ForeColor = Color.Firebrick
            lblStatus.Text = "Archive failed — nothing was removed from the database." & vbCrLf & ex.Message
        Finally
            Cursor = Cursors.Default
            btnArchive.Enabled = _total > 0
        End Try
    End Sub

End Class
