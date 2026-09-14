Imports System.Windows.Forms
Imports System.Drawing

''' Staff accounts list, Admin-only (frmMain hides this nav item otherwise —
''' re-check role here too if this control could ever be reached another way).
Public Class ucStaff
    Inherits UserControl

    Private ReadOnly grid As New PagedGrid() With {.PageSize = 5}
    Private btnAddStaff As New Button() With {.Text = "+ Add staff", .Tag = "primary", .AutoSize = True}
    Private btnToggle As New Button() With {.Text = "Enable/Disable", .AutoSize = True, .Enabled = False}
    Private btnExport As New Button() With {.Text = "Export CSV", .AutoSize = True}
    Private btnDelete As New Button() With {.Text = "Delete", .Tag = "danger", .AutoSize = True, .Enabled = False}
    Private toolbar As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(12)}

    Public Sub New()
        toolbar.Controls.Add(btnAddStaff)
        toolbar.Controls.Add(btnToggle)
        toolbar.Controls.Add(btnExport)
        toolbar.Controls.Add(btnDelete)
        Controls.Add(grid)
        Controls.Add(toolbar)

        AddHandler grid.Grid.SelectionChanged, Sub(s, e)
                                              Dim has = grid.Grid.SelectedRows.Count > 0
                                              btnToggle.Enabled = has
                                              btnDelete.Enabled = has
                                          End Sub
        AddHandler btnAddStaff.Click, AddressOf btnAddStaff_Click
        AddHandler btnToggle.Click, AddressOf btnToggle_Click
        AddHandler btnExport.Click, Sub(s, e) AppUI.ExportCsv(grid.AllRows(), "staff", FindForm())
        AddHandler btnDelete.Click, AddressOf btnDelete_Click
        AddHandler Me.Load, Sub(s, e) LoadGrid()
    End Sub

    ''' Accounts are shared by both businesses, so this screen lists and changes
    ''' them through Auth (the home database), whichever business is open.
    Private Sub btnDelete_Click(sender As Object, e As EventArgs)
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Dim username = Convert.ToString(grid.Grid.SelectedRows(0).Cells("Username").Value)
        Dim name = Convert.ToString(grid.Grid.SelectedRows(0).Cells("FullName").Value)
        If Not AppUI.Confirm(FindForm(), $"Delete staff account ""{name}""?" & vbCrLf & vbCrLf &
                             "They will no longer be able to sign in to either business. This cannot be undone.",
                             "Delete record", "Delete", danger:=True) Then Return
        Try
            If Auth.DeleteAccount(username) > 0 Then AppUI.Toast($"staff account ""{name}"" deleted.", AppUI.ToastKind.Success)
        Catch ex As Data.SqlClient.SqlException When ex.Number = 547
            AppUI.Toast($"Can't delete ""{name}"" — the account is linked to sales or stock records. Disable it instead.", AppUI.ToastKind.Error)
        End Try
        LoadGrid()
    End Sub

    Private Sub LoadGrid()
        grid.Bind(Auth.Accounts())
    End Sub

    Private Sub btnAddStaff_Click(sender As Object, e As EventArgs)
        Using f As New frmAddStaff()
            If f.ShowDialog() = DialogResult.OK Then
                ' The typed password is not stored as-is: the account is flagged so
                ' its owner sets a real one at first sign-in.
                Auth.AddAccount(f.FullName, f.Username, f.HashedPassword, f.Role, mustChange:=True)
                LoadGrid()
            End If
        End Using
    End Sub

    Private Sub btnToggle_Click(sender As Object, e As EventArgs)
        Auth.ToggleActive(Convert.ToString(grid.Grid.SelectedRows(0).Cells("Username").Value))
        LoadGrid()
    End Sub

End Class
