Imports System.Data
Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

''' Send a catalogue / price list to a customer: pick who it's for and which
''' prices they get, preview it, then download the PDF — which opens the email
''' app with the message ready and the folder with the file, to attach and send.
Public Class frmSendPriceList
    Inherits Form

    Private NotInheritable Class CustomerChoice
        Public Property Id As Integer
        Public Property Name As String
        Public Property Email As String
        Public Property CustomerType As String
        Public Overrides Function ToString() As String
            Return If(Id = 0, Name, Name & If(String.IsNullOrWhiteSpace(Email), "   (no email saved)", "   ·   " & Email))
        End Function
    End Class

    Private ReadOnly cboCustomer As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Dock = DockStyle.Fill}
    Private ReadOnly txtEmail As New TextBox() With {.Dock = DockStyle.Fill}
    Private ReadOnly cboTier As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Dock = DockStyle.Fill}
    Private ReadOnly chkOutOfStock As New CheckBox() With {.Text = "Include products that are out of stock", .AutoSize = True}
    Private ReadOnly lblSummary As New Label() With {.AutoSize = True, .MaximumSize = New Size(560, 0)}
    Private ReadOnly btnPreview As New Button() With {.Text = "Preview", .AutoSize = True}
    Private ReadOnly btnSend As New Button() With {.Text = "Download PDF && open email", .Tag = "primary", .AutoSize = True}
    Private ReadOnly btnClose As New Button() With {.Text = "Close", .AutoSize = True}

    Public Sub New(Optional customerId As Integer? = Nothing)
        Text = $"Send price list — {Company.Current.DisplayName}"
        Width = 680
        Height = 520
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False
        MaximizeBox = False

        cboCustomer.Items.Add(New CustomerChoice With {.Id = 0, .Name = "(Not for a particular customer)", .CustomerType = "Retailer"})
        For Each r As DataRow In DataAccess.GetTable("SELECT CustomerID, Name, ISNULL(Email,'') AS Email, CustomerType FROM Customers ORDER BY Name").Rows
            cboCustomer.Items.Add(New CustomerChoice With {
                .Id = Convert.ToInt32(r("CustomerID")), .Name = Convert.ToString(r("Name")),
                .Email = Convert.ToString(r("Email")), .CustomerType = Convert.ToString(r("CustomerType"))})
        Next
        cboTier.Items.AddRange(PriceLists.Tiers)

        Dim form As New TableLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 2, .Padding = New Padding(18, 16, 18, 6)}
        form.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        form.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        AddRow(form, "Customer", cboCustomer)
        AddRow(form, "Email to", txtEmail)
        AddRow(form, "Prices", cboTier)
        form.Controls.Add(New Label() With {.AutoSize = True}, 0, form.RowCount)
        form.Controls.Add(chkOutOfStock, 1, form.RowCount)
        form.RowCount += 1

        Dim how As New Label() With {
            .AutoSize = True, .MaximumSize = New Size(620, 0), .Margin = New Padding(18, 8, 18, 8),
            .Text = "How sending works: Download saves the PDF, then opens your email app with the message " &
                    "already written and the folder holding the PDF. Drag the PDF into the email, check it, and press Send."}
        Dim notes As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .AutoSize = True, .FlowDirection = FlowDirection.TopDown, .WrapContents = False, .Padding = New Padding(0, 0, 0, 6)}
        lblSummary.Margin = New Padding(18, 4, 18, 4)
        notes.Controls.Add(lblSummary)
        notes.Controls.Add(how)

        Dim buttons As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(12)}
        buttons.Controls.Add(btnClose)
        buttons.Controls.Add(btnSend)
        buttons.Controls.Add(btnPreview)

        Controls.Add(notes)
        Controls.Add(form)
        Controls.Add(buttons)

        AddHandler cboCustomer.SelectedIndexChanged, Sub(s, e) CustomerPicked()
        AddHandler cboTier.SelectedIndexChanged, Sub(s, e) Summarise()
        AddHandler chkOutOfStock.CheckedChanged, Sub(s, e) Summarise()
        AddHandler btnPreview.Click, Sub(s, e) Document().ShowPreview(Me)
        AddHandler btnSend.Click, AddressOf Send_Click
        AddHandler btnClose.Click, Sub(s, e) Close()

        Dim startAt = 0
        If customerId.HasValue Then
            For i = 1 To cboCustomer.Items.Count - 1
                If DirectCast(cboCustomer.Items(i), CustomerChoice).Id = customerId.Value Then startAt = i
            Next
        End If
        cboCustomer.SelectedIndex = startAt
        Theme.Apply(Me)
        UiHelpers.FitToScreen(Me)
    End Sub

    Private Shared Sub AddRow(table As TableLayoutPanel, caption As String, field As Control)
        Dim row = table.RowCount
        table.Controls.Add(New Label() With {.Text = caption, .AutoSize = True, .Margin = New Padding(0, 8, 12, 6)}, 0, row)
        field.Margin = New Padding(0, 4, 0, 6)
        table.Controls.Add(field, 1, row)
        table.RowCount += 1
    End Sub

    Private ReadOnly Property Chosen As CustomerChoice
        Get
            Return DirectCast(cboCustomer.SelectedItem, CustomerChoice)
        End Get
    End Property

    Private ReadOnly Property CustomerName As String
        Get
            Return If(Chosen Is Nothing OrElse Chosen.Id = 0, Nothing, Chosen.Name)
        End Get
    End Property

    ''' A customer brings their saved email and their own price tier with them.
    Private Sub CustomerPicked()
        If Chosen Is Nothing Then Return
        txtEmail.Text = If(Chosen.Email, "")
        cboTier.SelectedItem = PriceLists.TierFor(Chosen.CustomerType)
        Summarise()
    End Sub

    Private Sub Summarise()
        If cboTier.SelectedItem Is Nothing Then Return
        Dim count = PriceLists.Lines(CStr(cboTier.SelectedItem), chkOutOfStock.Checked).Rows.Count
        lblSummary.Text = $"{count} product(s) at {cboTier.SelectedItem} prices will be listed."
        btnSend.Enabled = count > 0
        btnPreview.Enabled = count > 0
    End Sub

    Private Function Document() As DocPrinter
        Return PriceLists.Build(CStr(cboTier.SelectedItem), CustomerName, chkOutOfStock.Checked)
    End Function

    Private Sub Send_Click(sender As Object, e As EventArgs)
        Dim email = txtEmail.Text.Trim()
        If email <> "" AndAlso Not PriceLists.IsPlausibleEmail(email) Then
            AppUI.Info(Me, $"'{email}' doesn't look like an email address. Correct it, or clear it to just download the PDF.")
            Return
        End If

        Dim doc = Document()
        Dim folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Company.Current.DisplayName & " price lists")
        Dim path_ As String
        Using sfd As New SaveFileDialog() With {
            .Filter = "PDF document (*.pdf)|*.pdf", .Title = "Save price list",
            .FileName = SafeName(doc.DocTitle) & ".pdf"}
            Try
                Directory.CreateDirectory(folder)
                sfd.InitialDirectory = folder
            Catch
            End Try
            If sfd.ShowDialog(Me) <> DialogResult.OK Then Return
            path_ = sfd.FileName
        End Using

        Cursor = Cursors.WaitCursor
        Dim err = doc.SaveAsPdfTo(path_)
        Cursor = Cursors.Default
        If err <> "" Then
            AppUI.Toast("Couldn't save the price list: " & err, AppUI.ToastKind.Error)
            Return
        End If
        AppUI.Toast("Saved " & Path.GetFileName(path_), AppUI.ToastKind.Success)

        ' The folder first, so the email window opens on top of it with the file in view.
        Try
            Process.Start("explorer.exe", $"/select,""{path_}""")
        Catch
        End Try
        If email <> "" Then
            Try
                Process.Start(New ProcessStartInfo(PriceLists.MailtoLink(email, PriceLists.EmailSubject(),
                    PriceLists.EmailBody(CStr(cboTier.SelectedItem), CustomerName))) With {.UseShellExecute = True})
            Catch ex As Exception
                AppUI.Info(Me, "The PDF is saved, but no email app opened (" & ex.Message & "). " &
                               "Attach the file from the folder that just opened to an email to " & email & ".")
            End Try
        End If
    End Sub

    Private Shared Function SafeName(name As String) As String
        For Each ch In Path.GetInvalidFileNameChars()
            name = name.Replace(ch, "_"c)
        Next
        Return name.Trim()
    End Function

End Class
