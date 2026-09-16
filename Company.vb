Imports System.Configuration
Imports System.Data.SqlClient
Imports System.Drawing
Imports System.IO

''' The two businesses ChewyStock runs: ChewyPets Farm & Feeds and Candid
''' Purrfect Pets. The sign-in screen picks one, and everything after that —
''' app name, logo, stock, sales, customers, attendance, receipts — belongs to
''' that business.
'''
''' Each business keeps its own database, with its own record numbering. Putting
''' both into one set of tables with a "which company" column would mean every
''' one of hundreds of queries had to remember the filter, and one forgotten
''' WHERE would print a Candid customer on a ChewyPets report. Separate
''' databases make that impossible.
'''
''' Sign-in accounts are the one thing shared: they live in the ChewyPets
''' database (the "home" database) and are mirrored into the other on sign-in,
''' so admin and clerk use one username and one password for both.
Public NotInheritable Class Company

    Public ReadOnly Property Key As String
    ''' Short name for the sign-in picker and file names.
    Public ReadOnly Property DisplayName As String
    ''' The app's own name while this business is signed into.
    Public ReadOnly Property AppName As String
    ''' Registered name, as printed on documents.
    Public ReadOnly Property LegalName As String
    ''' Start of every receipt / order number: "ChewyStock-14092026-143205".
    Public ReadOnly Property DocumentPrefix As String
    ''' Database name; Nothing means "whatever App.config names" (the home database).
    Private ReadOnly _database As String
    ''' App.config key prefix for this company's address/phone/bank settings.
    Private ReadOnly _settingsPrefix As String
    Private ReadOnly _signatureFile As String
    Private ReadOnly _waybillStampFile As String
    Private ReadOnly _logoFiles As String()
    Private ReadOnly _defaultBanks As (Bank As String, AccountName As String, AccountNumber As String)()
    ''' Whether this business keeps a price book (price updates with history)
    ''' and sends price lists to customers. Candid Purrfect only.
    Public ReadOnly Property HasPriceLists As Boolean

    Private Sub New(key As String, displayName As String, appName As String, legalName As String, documentPrefix As String,
                    database As String, settingsPrefix As String, signatureFile As String, logoFiles As String(),
                    hasPriceLists As Boolean,
                    defaultBanks As (Bank As String, AccountName As String, AccountNumber As String)(),
                    Optional waybillStampFile As String = Nothing)
        Me.Key = key
        Me.DisplayName = displayName
        Me.AppName = appName
        Me.LegalName = legalName
        Me.DocumentPrefix = documentPrefix
        _database = database
        _settingsPrefix = settingsPrefix
        _signatureFile = signatureFile
        _waybillStampFile = If(waybillStampFile, signatureFile)
        _logoFiles = logoFiles
        Me.HasPriceLists = hasPriceLists
        _defaultBanks = defaultBanks
    End Sub

    Public Shared ReadOnly ChewyPets As New Company(
        "chewypets", "ChewyPets Feed", appName:="ChewyStock", legalName:="ChewyPets Farm & Feeds Company Ltd",
        documentPrefix:="ChewyStock", database:=Nothing, settingsPrefix:="", signatureFile:="signature.jpeg",
        logoFiles:={"chewypetfeedslogo.jpeg", "logo.png"}, hasPriceLists:=False, defaultBanks:={},
        waybillStampFile:="waybillrecipt for chewypet.jpeg")

    Public Shared ReadOnly CandidPurrfect As New Company(
        "candid", "Candid Purrfect", appName:="Candid Purrfect", legalName:="Candid Purrfect Pets Company Ltd",
        documentPrefix:="CandidPurrfect", database:="CandidPurrfectDB", settingsPrefix:="Candid",
        signatureFile:="candidPurffect.jpeg", logoFiles:={"candidPurffectlogo.jpeg"}, hasPriceLists:=True,
        defaultBanks:={
            ("Sterling Bank PLC", "Candid Purrfect Pets Company Ltd", "0097166161"),
            ("First Bank PLC", "Candid Purrfect Pets Company Ltd", "2045958847")},
        waybillStampFile:="waybillrecipt for candid.jpeg")

    Public Shared ReadOnly Property All As Company() = {ChewyPets, CandidPurrfect}

    ''' Where sign-in accounts live.
    Public Shared ReadOnly Property Home As Company = ChewyPets

    Private Shared _current As Company = ChewyPets
    ''' The business signed into. Home until someone signs in to another.
    Public Shared ReadOnly Property Current As Company
        Get
            Return _current
        End Get
    End Property

    ''' Switch the whole app to `company`. Only the sign-in screen does this.
    Public Shared Sub Use(company As Company)
        If company Is Nothing OrElse company Is _current Then Return
        _current = company
        RaiseEvent Changed(Nothing, EventArgs.Empty)
    End Sub

    Public Shared Event Changed As EventHandler

    Public Shared Function FromKey(key As String) As Company
        Return All.FirstOrDefault(Function(c) String.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
    End Function

    Public ReadOnly Property IsHome As Boolean
        Get
            Return Me Is Home
        End Get
    End Property

    ' ===== database =====

    Private Shared ReadOnly Property ConfiguredConnection As String
        Get
            Return ConfigurationManager.ConnectionStrings("StockDeskDB").ConnectionString
        End Get
    End Property

    ''' Same server and credentials as App.config — only the database differs.
    Public ReadOnly Property ConnectionString As String
        Get
            If _database Is Nothing Then Return ConfiguredConnection
            Return New SqlConnectionStringBuilder(ConfiguredConnection) With {.InitialCatalog = _database}.ConnectionString
        End Get
    End Property

    Public ReadOnly Property DatabaseName As String
        Get
            Return New SqlConnectionStringBuilder(ConnectionString).InitialCatalog
        End Get
    End Property

    ' ===== identity printed on documents =====

    ''' Setting for this company: "CandidCompanyAddress" for Candid, plain
    ''' "CompanyAddress" for ChewyPets. Candid never falls back to a ChewyPets
    ''' value — a blank line beats the other company's address on a receipt.
    Public Function Setting(name As String, fallback As String) As String
        Dim v = ConfigurationManager.AppSettings(_settingsPrefix & name)
        If String.IsNullOrWhiteSpace(v) OrElse v.TrimStart().StartsWith("[") Then Return fallback
        Return v
    End Function

    ''' Name on documents and the app header. ChewyPets keeps its configured
    ''' name (the activation key is issued against it elsewhere, so it can't move).
    Public ReadOnly Property DocumentName As String
        Get
            Return If(IsHome, Setting("CompanyName", DisplayName), Setting("CompanyName", LegalName))
        End Get
    End Property

    Public Function BankAccounts() As List(Of (Bank As String, AccountName As String, AccountNumber As String))
        Dim list As New List(Of (Bank As String, AccountName As String, AccountNumber As String))
        Dim prefixes = {"Bank", "Bank2", "Bank3"}
        For i = 0 To prefixes.Length - 1
            Dim fallback = If(i < _defaultBanks.Length, _defaultBanks(i), ("", "", ""))
            Dim number = Setting(prefixes(i) & "AccountNumber", fallback.Item3)
            If number = "" Then Continue For
            list.Add((Setting(prefixes(i) & "Name", fallback.Item1),
                      Setting(prefixes(i) & "AccountName", fallback.Item2), number))
        Next
        Return list
    End Function

    ''' "ChewyPetsFeed" / "CandidPurrfect" — for export file names.
    Public ReadOnly Property FilePrefix As String
        Get
            Return New String(DocumentName.Where(Function(ch) Char.IsLetterOrDigit(ch)).Take(24).ToArray())
        End Get
    End Property

    ' ===== artwork =====

    Private _logo As Image
    Private _logoTried As Boolean
    ''' The business's own logo, with its name in the artwork. The first file
    ''' that exists wins, so an older install without the new artwork still
    ''' shows something.
    Public ReadOnly Property Logo As Image
        Get
            If Not _logoTried Then
                _logoTried = True
                For Each f In _logoFiles
                    _logo = LoadImage(f, trim:=True)
                    If _logo IsNot Nothing Then Exit For
                Next
            End If
            Return _logo
        End Get
    End Property

    Private _signature As Image
    Private _signatureTried As Boolean
    ''' The stamp and signature printed on this business's receipts.
    Public ReadOnly Property Signature As Image
        Get
            If Not _signatureTried Then
                _signatureTried = True
                _signature = LoadImage(_signatureFile, trim:=True)
            End If
            Return _signature
        End Get
    End Property

    Public ReadOnly Property SignatureFile As String
        Get
            Return _signatureFile
        End Get
    End Property

    Private _waybillStamp As Image
    Private _waybillStampTried As Boolean
    ''' The "confirmed and released" stamp printed on this business's waybills
    ''' only — distinct from the receipt Signature, so a delivery note can be
    ''' company-stamped without the sales-receipt artwork bleeding into it.
    Public ReadOnly Property WaybillStamp As Image
        Get
            If Not _waybillStampTried Then
                _waybillStampTried = True
                _waybillStamp = LoadImage(_waybillStampFile, trim:=True)
            End If
            Return _waybillStamp
        End Get
    End Property

    Private Shared Function LoadImage(fileName As String, trim As Boolean) As Image
        Try
            Dim p = AppPaths.Asset(fileName)
            If Not File.Exists(p) Then Return Nothing
            ' Read through a copy so the file isn't held open for the app's lifetime.
            Using original = Image.FromFile(p)
                Dim copy As New Bitmap(original)
                Return If(trim, TrimMargins(DropFrame(copy)), copy)
            End Using
        Catch
            Return Nothing
        End Try
    End Function

    ''' Some artwork arrives with a thin scanner/export frame round a white page
    ''' (the ChewyPet logo does). Left in, it stops the white margin being
    ''' trimmed. Only light artwork is touched — a logo on black is its own design.
    Friend Shared Function DropFrame(source As Bitmap) As Bitmap
        Dim inset = Math.Max(2, CInt(Math.Min(source.Width, source.Height) * 0.015))
        If source.Width <= inset * 4 OrElse source.Height <= inset * 4 Then Return source
        Dim inside = source.GetPixel(inset * 2, inset * 2)
        If inside.R < 235 OrElse inside.G < 235 OrElse inside.B < 235 Then Return source
        Dim cropped = source.Clone(New Rectangle(inset, inset, source.Width - inset * 2, source.Height - inset * 2), source.PixelFormat)
        source.Dispose()
        Return cropped
    End Function

    ''' Cuts the white border off artwork. The stamps arrive as a circle in the
    ''' middle of a wide white photo; untrimmed, the circle would print at a
    ''' fraction of the space it's given. Dark artwork has no white border and
    ''' comes back untouched.
    Public Shared Function TrimMargins(source As Bitmap) As Bitmap
        Dim isInk = Function(c As Color) c.A > 40 AndAlso (c.R < 215 OrElse c.G < 215 OrElse c.B < 215)
        ' Sampling every other pixel is plenty to find a border and keeps load instant.
        Const Stride As Integer = 2
        Dim left = source.Width, top = source.Height, right = -1, bottom = -1
        For y = 0 To source.Height - 1 Step Stride
            For x = 0 To source.Width - 1 Step Stride
                If isInk(source.GetPixel(x, y)) Then
                    If x < left Then left = x
                    If x > right Then right = x
                    If y < top Then top = y
                    If y > bottom Then bottom = y
                End If
            Next
        Next
        If right < 0 Then Return source
        Const Pad As Integer = 6
        Dim area = Rectangle.FromLTRB(Math.Max(0, left - Pad), Math.Max(0, top - Pad),
                                      Math.Min(source.Width, right + Pad + 1), Math.Min(source.Height, bottom + Pad + 1))
        If area.Width >= source.Width - 2 AndAlso area.Height >= source.Height - 2 Then Return source
        Dim cropped = source.Clone(area, source.PixelFormat)
        source.Dispose()
        Return cropped
    End Function

    ' ===== remembered choice =====

    Private Shared ReadOnly Property ChoiceFile As String
        Get
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StockDesk", "last-company.txt")
        End Get
    End Property

    ''' The business last signed into on this PC, so the picker opens where people left it.
    Public Shared Function LastUsed() As Company
        Try
            If File.Exists(ChoiceFile) Then Return If(FromKey(File.ReadAllText(ChoiceFile).Trim()), Home)
        Catch
        End Try
        Return Home
    End Function

    Public Shared Sub Remember(company As Company)
        Try
            Directory.CreateDirectory(Path.GetDirectoryName(ChoiceFile))
            File.WriteAllText(ChoiceFile, company.Key)
        Catch
        End Try
    End Sub

    Public Overrides Function ToString() As String
        Return DisplayName
    End Function

End Class
