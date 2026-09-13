Imports System.Windows.Forms
Imports System.Drawing
Imports System.IO
Imports System.Text
Imports System.Data

''' Shared UI feedback + data-export helpers used across every screen:
'''   • Toast.Show(...)      — brief non-blocking corner notification (auto-fades)
'''   • AppUI.Confirm(...)   — themed yes/no modal, returns True on confirm
'''   • AppUI.Info(...)      — themed OK modal
'''   • AppUI.ExportCsv(...) — dump a grid or DataTable to a .csv the user picks
Public Module AppUI

    ' ===== Toasts =====
    Public Enum ToastKind
        Info = 0
        Success = 1
        [Warning] = 2
        [Error] = 3
    End Enum

    Private ReadOnly _openToasts As New List(Of Form)()

    Public Sub Toast(message As String, Optional kind As ToastKind = ToastKind.Info, Optional owner As IWin32Window = Nothing)
        ShowToast(message, kind, TryCast(owner, Form))
    End Sub

    ''' Alias so call sites can read Toast.Show(...) too.
    Public Sub Show(message As String, Optional kind As ToastKind = ToastKind.Info, Optional owner As Form = Nothing)
        ShowToast(message, kind, owner)
    End Sub

    Private Sub ShowToast(message As String, kind As ToastKind, owner As Form)
        Dim host As Form = If(owner, Form.ActiveForm)
        Dim accent As Color
        Select Case kind
            Case ToastKind.Success : accent = Theme.FromHex("#2E7D32")
            Case ToastKind.Warning : accent = Theme.FromHex("#B26A00")
            Case ToastKind.Error : accent = Theme.Current.Danger
            Case Else : accent = Theme.Current.Primary
        End Select

        Dim f As New ToastForm(message, accent)

        Dim area = If(host IsNot Nothing AndAlso host.Visible, host.Bounds, Screen.PrimaryScreen.WorkingArea)
        Dim margin = 18
        Dim x = area.Right - f.Width - margin
        Dim y = area.Bottom - f.Height - margin - (_openToasts.Count * (f.Height + 8))
        f.Location = New Point(x, y)

        _openToasts.Add(f)
        AddHandler f.FormClosed, Sub(s, e) _openToasts.Remove(f)
        f.Show()
    End Sub

    ''' Borderless auto-fading notification window. Shown without stealing focus.
    Private NotInheritable Class ToastForm
        Inherits Form

        Private ReadOnly _life As New Timer() With {.Interval = 2600}
        Private ReadOnly _fade As New Timer() With {.Interval = 40}

        Public Sub New(message As String, accent As Color)
            FormBorderStyle = FormBorderStyle.None
            ShowInTaskbar = False
            TopMost = True
            StartPosition = FormStartPosition.Manual
            BackColor = Theme.Current.Surface
            Padding = New Padding(2)

            Dim stripe As New Panel() With {.Dock = DockStyle.Left, .Width = 5, .BackColor = accent}
            Dim lbl As New Label() With {
                .Text = message, .AutoSize = False, .Dock = DockStyle.Fill,
                .Padding = New Padding(14, 12, 16, 12), .ForeColor = Theme.Current.TextPrimary,
                .Font = New Font("Segoe UI", Math.Max(9, Theme.BaseFontSize - 1)),
                .BackColor = Theme.Current.Surface
            }
            Dim outline As New Panel() With {.Dock = DockStyle.Fill, .BackColor = Theme.Current.GridLineColor, .Padding = New Padding(1)}
            Dim inner As New Panel() With {.Dock = DockStyle.Fill, .BackColor = Theme.Current.Surface}
            inner.Controls.Add(lbl)
            inner.Controls.Add(stripe)
            outline.Controls.Add(inner)
            Controls.Add(outline)

            Dim g = CreateGraphics()
            Dim sz = g.MeasureString(message, lbl.Font, 320)
            g.Dispose()
            Width = Math.Min(380, Math.Max(220, CInt(sz.Width) + 60))
            Height = Math.Max(56, CInt(sz.Height) + 30)

            AddHandler _life.Tick, Sub(s, e)
                                       _life.Stop()
                                       _fade.Start()
                                   End Sub
            AddHandler _fade.Tick, Sub(s, e)
                                       Opacity -= 0.12
                                       If Opacity <= 0.05 Then
                                           _fade.Stop()
                                           Close()
                                       End If
                                   End Sub
            AddHandler Me.Click, Sub(s, e) Close()
            AddHandler lbl.Click, Sub(s, e) Close()
            AddHandler Me.Shown, Sub(s, e) _life.Start()
        End Sub

        ' Don't steal focus from the app when shown.
        Protected Overrides ReadOnly Property ShowWithoutActivation As Boolean
            Get
                Return True
            End Get
        End Property

        Protected Overrides ReadOnly Property CreateParams As CreateParams
            Get
                Const WS_EX_NOACTIVATE = &H8000000
                Const WS_EX_TOOLWINDOW = &H80
                Dim cp = MyBase.CreateParams
                cp.ExStyle = cp.ExStyle Or WS_EX_NOACTIVATE Or WS_EX_TOOLWINDOW
                Return cp
            End Get
        End Property
    End Class

    ' ===== Modal dialogs (themed) =====

    Public Function Confirm(owner As IWin32Window, message As String,
                            Optional title As String = "Please confirm",
                            Optional confirmText As String = "Yes, continue",
                            Optional danger As Boolean = False) As Boolean
        Using f = BuildModal(title, message, confirmText, "Cancel", danger, showCancel:=True)
            Return f.ShowDialog(owner) = DialogResult.OK
        End Using
    End Function

    Public Sub Info(owner As IWin32Window, message As String, Optional title As String = "Notice")
        Using f = BuildModal(title, message, "OK", Nothing, danger:=False, showCancel:=False)
            f.ShowDialog(owner)
        End Using
    End Sub

    Private Function BuildModal(title As String, message As String, okText As String, cancelText As String,
                                danger As Boolean, showCancel As Boolean) As Form
        Dim f As New Form() With {
            .Text = title, .FormBorderStyle = FormBorderStyle.FixedDialog,
            .StartPosition = FormStartPosition.CenterParent, .MinimizeBox = False, .MaximizeBox = False,
            .ClientSize = New Size(420, 170), .BackColor = Theme.Current.WindowBg
        }

        Dim msg As New Label() With {
            .Text = message, .Dock = DockStyle.Fill, .Padding = New Padding(22, 22, 22, 10),
            .Font = Theme.BaseFont(), .ForeColor = Theme.Current.TextPrimary, .UseMnemonic = False
        }
        ' Grow taller for long messages instead of clipping them.
        Dim textH = TextRenderer.MeasureText(message, msg.Font, New Size(376, 0), TextFormatFlags.WordBreak).Height
        f.ClientSize = New Size(420, Math.Max(170, textH + 32 + 80))

        Dim bar As New FlowLayoutPanel() With {
            .Dock = DockStyle.Bottom, .FlowDirection = FlowDirection.RightToLeft,
            .AutoSize = True, .Padding = New Padding(16)
        }
        Dim btnOk As New Button() With {.Text = okText, .AutoSize = True, .DialogResult = DialogResult.OK, .Tag = "primary"}
        If danger Then Theme.StyleDangerButton(btnOk) Else Theme.StylePrimaryButton(btnOk)
        bar.Controls.Add(btnOk)
        f.AcceptButton = btnOk

        If showCancel Then
            Dim btnCancel As New Button() With {.Text = cancelText, .AutoSize = True, .DialogResult = DialogResult.Cancel}
            btnCancel.Font = Theme.BaseFont()
            bar.Controls.Add(btnCancel)
            f.CancelButton = btnCancel
        End If

        f.Controls.Add(msg)
        f.Controls.Add(bar)
        Return f
    End Function

    ' ===== Admin delete =====

    ''' Confirm + run a DELETE. Translates a foreign-key violation into a plain
    ''' "still in use" message instead of a raw SQL error. Returns True if a row
    ''' was actually deleted (caller should then refresh its grid).
    Public Function TryDelete(owner As IWin32Window, whatLabel As String,
                              deleteSql As String, params As Dictionary(Of String, Object)) As Boolean
        If Not Confirm(owner, $"Delete {whatLabel}?" & vbCrLf & vbCrLf &
                       "This cannot be undone.", "Delete record", "Delete", danger:=True) Then
            Return False
        End If
        Try
            Dim n = DataAccess.Execute(deleteSql, params)
            If n > 0 Then
                Toast($"{whatLabel} deleted.", ToastKind.Success)
                Return True
            End If
            Toast("Nothing was deleted.", ToastKind.Warning)
            Return False
        Catch ex As Data.SqlClient.SqlException When ex.Number = 547 ' FK constraint
            Toast($"Can't delete {whatLabel} — it's still linked to other records " &
                  "(invoices, stock, payments, …).", ToastKind.Error)
            Return False
        Catch ex As Exception
            Toast("Delete failed: " & ex.Message, ToastKind.Error)
            Return False
        End Try
    End Function

    ' ===== CSV export =====

    Public Sub ExportCsv(grid As DataGridView, suggestedName As String, Optional owner As IWin32Window = Nothing)
        If grid Is Nothing OrElse grid.Columns.Count = 0 Then
            Toast("Nothing to export.", ToastKind.Warning)
            Return
        End If

        Dim cols = grid.Columns.Cast(Of DataGridViewColumn)().Where(Function(c) c.Visible).OrderBy(Function(c) c.DisplayIndex).ToList()
        Dim sb As New StringBuilder()
        sb.AppendLine(String.Join(",", cols.Select(Function(c) CsvField(c.HeaderText))))
        For Each row As DataGridViewRow In grid.Rows
            If row.IsNewRow Then Continue For
            sb.AppendLine(String.Join(",", cols.Select(Function(c) CsvField(Convert.ToString(row.Cells(c.Index).Value)))))
        Next

        WriteCsv(sb.ToString(), suggestedName, grid.Rows.Count, owner)
    End Sub

    Public Sub ExportCsv(table As DataTable, suggestedName As String, Optional owner As IWin32Window = Nothing)
        If table Is Nothing OrElse table.Columns.Count = 0 Then
            Toast("Nothing to export.", ToastKind.Warning)
            Return
        End If
        Dim sb As New StringBuilder()
        sb.AppendLine(String.Join(",", table.Columns.Cast(Of DataColumn)().Select(Function(c) CsvField(c.ColumnName))))
        For Each r As DataRow In table.Rows
            sb.AppendLine(String.Join(",", table.Columns.Cast(Of DataColumn)().Select(Function(c) CsvField(Convert.ToString(r(c))))))
        Next
        WriteCsv(sb.ToString(), suggestedName, table.Rows.Count, owner)
    End Sub

    Private Sub WriteCsv(content As String, suggestedName As String, rowCount As Integer, owner As IWin32Window)
        Using sfd As New SaveFileDialog()
            sfd.Filter = "CSV file (*.csv)|*.csv"
            sfd.FileName = Sanitize(suggestedName) & "_" & DateTime.Now.ToString("yyyyMMdd") & ".csv"
            If sfd.ShowDialog(owner) <> DialogResult.OK Then Return
            Try
                File.WriteAllText(sfd.FileName, content, New UTF8Encoding(True))
                Toast($"Exported {rowCount} row(s) to {Path.GetFileName(sfd.FileName)}", ToastKind.Success)
            Catch ex As Exception
                Toast("Export failed: " & ex.Message, ToastKind.Error)
            End Try
        End Using
    End Sub

    Public Function CsvField(value As String) As String
        If value Is Nothing Then Return ""
        If value.IndexOfAny(New Char() {","c, """"c, ChrW(10), ChrW(13)}) >= 0 Then
            Return """" & value.Replace("""", """""") & """"
        End If
        Return value
    End Function

    Private Function Sanitize(name As String) As String
        For Each ch In Path.GetInvalidFileNameChars()
            name = name.Replace(ch, "_"c)
        Next
        Return name.Replace(" ", "_")
    End Function

End Module
