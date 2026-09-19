Imports System.Windows.Forms
Imports System.Data

''' Move stock between warehouses — top up Shore from Lawal when Shore runs
''' out, or send Shore's goods back to Lawal when Lawal is short for an order.
''' The total held doesn't change. Any signed-in user can move stock.
Public Class frmMoveStock
    Inherits Form

    Private ReadOnly _userId As Integer
    Private cboProduct As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private cboFrom As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private cboTo As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private numQty As New NumericUpDown() With {.Minimum = 1, .Maximum = 1000000, .Value = 1}
    Private btnSwap As New Button() With {.Text = "⇅ Swap direction", .AutoSize = True}
    Private lblStock As New Label() With {.AutoSize = True, .Tag = "keepfont"}
    Private flow As New FlowStrip() With {.Dock = DockStyle.Top, .Height = 64}

    Public Sub New(userId As Integer)
        _userId = userId
        Text = "Move stock between warehouses"
        Width = 540
        Height = 440
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False

        cboProduct.DataSource = DataAccess.GetTable("SELECT ProductID, Name, Unit FROM Products WHERE IsActive = 1 ORDER BY Name")
        cboProduct.DisplayMember = "Name"
        cboProduct.ValueMember = "ProductID"

        ' Two separate tables so the combos don't share one selection.
        cboFrom.DataSource = DataAccess.GetTable("SELECT WarehouseID, Name FROM Warehouses ORDER BY Name")
        cboFrom.DisplayMember = "Name" : cboFrom.ValueMember = "WarehouseID"
        cboTo.DataSource = DataAccess.GetTable("SELECT WarehouseID, Name FROM Warehouses ORDER BY Name")
        cboTo.DisplayMember = "Name" : cboTo.ValueMember = "WarehouseID"

        Dim t = UiHelpers.NewFormTable()
        UiHelpers.AddLabeled(t, "Product", cboProduct)
        UiHelpers.AddLabeled(t, "From warehouse", cboFrom)
        UiHelpers.AddLabeled(t, "To warehouse", cboTo)
        UiHelpers.AddLabeled(t, "", btnSwap).Dock = DockStyle.None
        UiHelpers.AddLabeled(t, "Quantity to move", numQty)

        Dim head As New Label() With {.Dock = DockStyle.Top, .AutoSize = True, .Tag = "keepfont", .MaximumSize = New Drawing.Size(500, 0),
            .Padding = New Padding(16, 10, 16, 8),
            .Text = "Takes the quantity out of one warehouse and adds it to the other. The total stock stays the same."}
        Dim foot As New Panel() With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(16, 0, 16, 8)}
        foot.Controls.Add(lblStock)

        Controls.Add(foot)
        Controls.Add(t)
        Controls.Add(flow)
        Controls.Add(head)
        Height += flow.Height
        UiHelpers.AddOkCancelRow(Me, "Move stock", AddressOf Save_Click)

        AddHandler cboProduct.SelectedIndexChanged, Sub(s, e) ShowStock()
        AddHandler cboFrom.SelectedIndexChanged, Sub(s, e) ShowStock()
        AddHandler cboTo.SelectedIndexChanged, Sub(s, e) ShowStock()
        AddHandler btnSwap.Click, Sub(s, e)
                                      Dim f = cboFrom.SelectedValue
                                      cboFrom.SelectedValue = cboTo.SelectedValue
                                      cboTo.SelectedValue = f
                                  End Sub
        AddHandler Load, Sub(s, e)
                             ' Usual direction: production lands in Lawal, Shore gets topped up from it.
                             SelectWarehouse(cboFrom, "Lawal warehouse")
                             SelectWarehouse(cboTo, "Shore warehouse")
                             If cboTo.SelectedIndex = cboFrom.SelectedIndex AndAlso cboTo.Items.Count > 1 Then
                                 cboTo.SelectedIndex = If(cboFrom.SelectedIndex = 0, 1, 0)
                             End If
                             ShowStock()
                         End Sub

        UiHelpers.MakeScrollable(Me)
        Theme.Apply(Me)
    End Sub

    Private Shared Sub SelectWarehouse(cbo As ComboBox, name As String)
        For i = 0 To cbo.Items.Count - 1
            If String.Equals(Convert.ToString(CType(cbo.Items(i), DataRowView)("Name")), name, StringComparison.OrdinalIgnoreCase) Then
                cbo.SelectedIndex = i
                Return
            End If
        Next
    End Sub

    Private Sub ShowStock()
        If cboProduct.SelectedValue Is Nothing OrElse cboFrom.SelectedValue Is Nothing OrElse cboTo.SelectedValue Is Nothing Then Return
        flow.SetEnds(ShortName(cboFrom.Text), ShortName(cboTo.Text))
        Try
            Dim p = CInt(cboProduct.SelectedValue)
            Dim unit = Convert.ToString(CType(cboProduct.SelectedItem, DataRowView)("Unit")).ToLowerInvariant()
            lblStock.Text = $"{cboFrom.Text} has {Stock.QuantityInWarehouse(p, CInt(cboFrom.SelectedValue)):#,0} {unit}(s)   ·   " &
                            $"{cboTo.Text} has {Stock.QuantityInWarehouse(p, CInt(cboTo.SelectedValue)):#,0} {unit}(s)"
        Catch
            lblStock.Text = ""
        End Try
    End Sub

    Private Sub Save_Click(sender As Object, e As EventArgs)
        If cboProduct.SelectedValue Is Nothing OrElse cboFrom.SelectedValue Is Nothing OrElse cboTo.SelectedValue Is Nothing Then Return
        Dim qty = CInt(numQty.Value)
        Dim err = Stock.TransferStock(CInt(cboProduct.SelectedValue), CInt(cboFrom.SelectedValue), CInt(cboTo.SelectedValue), qty, _userId)
        If err <> "" Then
            AppUI.Toast("Could not move stock: " & err, AppUI.ToastKind.Error)
            ShowStock()
            Return
        End If
        AppUI.Toast($"Moved {qty:#,0} from {cboFrom.Text} to {cboTo.Text}.", AppUI.ToastKind.Success)
        Anim.SuccessTick(If(Owner, Me), "Stock moved")
        DialogResult = DialogResult.OK
        Close()
    End Sub

    Private Shared Function ShortName(warehouse As String) As String
        Return warehouse.Replace(" warehouse", "").Replace(" Warehouse", "")
    End Function

    ''' "LAWAL  ● ● ● ●  ➜  SHORE" — dots flow from the sending warehouse to
    ''' the receiving one, and reverse when the direction is swapped.
    Private NotInheritable Class FlowStrip
        Inherits Control
        Private _from As String = "", _to As String = ""
        Private ReadOnly _clock As Diagnostics.Stopwatch = Diagnostics.Stopwatch.StartNew()
        Private ReadOnly _timer As New Timer() With {.Interval = 30}
        Private _flipAt As Long = -10000

        Public Sub New()
            DoubleBuffered = True
            SetStyle(ControlStyles.SupportsTransparentBackColor, True)
            AddHandler _timer.Tick, Sub(s, e) Invalidate()
            If Anim.Enabled Then _timer.Start()
        End Sub

        Public Sub SetEnds(fromName As String, toName As String)
            If fromName = _from AndAlso toName = _to Then Return
            If _from <> "" Then _flipAt = _clock.ElapsedMilliseconds
            _from = fromName : _to = toName
            Invalidate()
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias
            g.Clear(If(Parent IsNot Nothing, Parent.BackColor, BackColor))
            Dim accent = Theme.Current.Primary
            Dim font As New Drawing.Font("Segoe UI", 10, Drawing.FontStyle.Bold)
            Dim cy = Height \ 2
            Const pad = 24, pill = 92

            ' A quick pop on both ends right after the direction changes.
            Dim sinceFlip = (_clock.ElapsedMilliseconds - _flipAt) / 300.0
            Dim pop = If(sinceFlip < 1, 1 + 0.12 * Math.Sin(sinceFlip * Math.PI), 1.0)

            DrawPill(g, New Drawing.Rectangle(pad, cy - 17, pill, 34), _from.ToUpperInvariant(), accent, font, pop, filled:=False)
            DrawPill(g, New Drawing.Rectangle(Width - pad - pill, cy - 17, pill, 34), _to.ToUpperInvariant(), accent, font, pop, filled:=True)

            Dim x0 = pad + pill + 14, x1 = Width - pad - pill - 22
            If x1 <= x0 Then Return
            Using track As New Drawing.Pen(Drawing.Color.FromArgb(50, accent), 2) With {.DashStyle = Drawing.Drawing2D.DashStyle.Dot}
                g.DrawLine(track, x0, cy, x1, cy)
            End Using
            ' Moving dots, brightest in the middle of the run.
            Dim spacing = 26.0
            Dim offset = If(Anim.Enabled, (_clock.ElapsedMilliseconds / 22.0) Mod spacing, 0)
            Dim x = x0 + offset
            While x < x1
                Dim along = (x - x0) / (x1 - x0)
                Dim alpha = CInt(60 + 195 * Math.Sin(along * Math.PI))
                Using b As New Drawing.SolidBrush(Drawing.Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), accent))
                    g.FillEllipse(b, CSng(x - 4), cy - 4, 8, 8)
                End Using
                x += spacing
            End While
            ' Arrow head
            Using b As New Drawing.SolidBrush(accent)
                g.FillPolygon(b, {New Drawing.PointF(x1 + 14, cy), New Drawing.PointF(x1, cy - 8), New Drawing.PointF(x1, cy + 8)})
            End Using
            font.Dispose()
        End Sub

        Private Shared Sub DrawPill(g As Drawing.Graphics, r As Drawing.Rectangle, text As String, accent As Drawing.Color,
                                    font As Drawing.Font, scale As Double, filled As Boolean)
            Dim cx = r.X + r.Width / 2.0, cy = r.Y + r.Height / 2.0
            Dim w = r.Width * scale, h = r.Height * scale
            Dim rr As New Drawing.Rectangle(CInt(cx - w / 2), CInt(cy - h / 2), CInt(w), CInt(h))
            Using gp = Anim.RoundedRect(rr, CInt(h / 2) - 1)
                If filled Then
                    Using b As New Drawing.SolidBrush(accent)
                        g.FillPath(b, gp)
                    End Using
                End If
                Using p As New Drawing.Pen(accent, 2)
                    g.DrawPath(p, gp)
                End Using
            End Using
            Using b As New Drawing.SolidBrush(If(filled, Drawing.Color.White, accent)),
                  sf As New Drawing.StringFormat() With {.Alignment = Drawing.StringAlignment.Center, .LineAlignment = Drawing.StringAlignment.Center}
                g.DrawString(text, font, b, New Drawing.RectangleF(rr.X, rr.Y, rr.Width, rr.Height), sf)
            End Using
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then _timer.Dispose()
            MyBase.Dispose(disposing)
        End Sub
    End Class

End Class
