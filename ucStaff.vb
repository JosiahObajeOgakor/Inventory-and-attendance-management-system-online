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

    Private Sub btnDelete_Click(sender As Object, e As EventArgs)
        If grid.Grid.SelectedRows.Count = 0 Then Return
        Dim userId = CInt(grid.Grid.SelectedRows(0).Cells("UserID").Value)
        Dim name = Convert.ToString(grid.Grid.SelectedRows(0).Cells("FullName").Value)
        If AppUI.TryDelete(FindForm(), $"staff account ""{name}""",
                           "DELETE FROM Users WHERE UserID = @id",
                           New Dictionary(Of String, Object) From {{"@id", userId}}) Then
            LoadGrid()
        End If
    End Sub

    Private Sub LoadGrid()
        Dim sql = "SELECT u.UserID, u.FullName, u.Username, r.RoleName, " &
                  "CASE WHEN u.IsActive = 1 THEN 'Active' ELSE 'Disabled' END AS StatusLabel " &
                  "FROM Users u JOIN Roles r ON r.RoleID = u.RoleID ORDER BY u.FullName"
        grid.Bind(DataAccess.GetTable(sql), hiddenColumns:={"UserID"})
    End Sub

    Private Sub btnAddStaff_Click(sender As Object, e As EventArgs)
        Using f As New frmAddStaff()
            If f.ShowDialog() = DialogResult.OK Then
                ' TODO: hash f.Password with BCrypt/PBKDF2 before storing — never plain text.
                DataAccess.Execute(
                    "INSERT INTO Users (FullName, Username, PasswordHash, RoleID, IsActive) " &
                    "VALUES (@name, @username, @hash, (SELECT RoleID FROM Roles WHERE RoleName = @role), 1)",
                    New Dictionary(Of String, Object) From {
                        {"@name", f.FullName}, {"@username", f.Username}, {"@hash", f.HashedPassword}, {"@role", f.Role}
                    })
                LoadGrid()
            End If
        End Using
    End Sub

    Private Sub btnToggle_Click(sender As Object, e As EventArgs)
        Dim userId = CInt(grid.Grid.SelectedRows(0).Cells("UserID").Value)
        DataAccess.Execute("UPDATE Users SET IsActive = 1 - IsActive WHERE UserID = @id",
            New Dictionary(Of String, Object) From {{"@id", userId}})
        LoadGrid()
    End Sub

End Class
