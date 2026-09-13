Imports System.Drawing
Imports System.Windows.Forms
Imports System.IO

''' Central look-and-feel for the whole app. One place decides fonts, colours and
''' table styling; every screen calls Theme.Apply(Me) / UiHelpers.NewGrid() and
''' inherits it. Users change the active palette + table design on the Appearance
''' screen (frmAppearance); choices are saved to the AppSettings table so the whole
''' office shares one look. Raise/handle ThemeChanged to restyle open windows live.
Public Module Theme

    ' ===== Branding =====
    Public ReadOnly Property AppName As String = "ChewyStock"

    ''' The company logo, loaded once from Assets\logo.png next to the exe.
    ''' Drop the artwork there (any size; 512px square works well). If it's
    ''' missing the app falls back to a drawn wordmark, so this never crashes.
    Private _logo As Image
    Private _logoTried As Boolean
    Public ReadOnly Property Logo As Image
        Get
            If Not _logoTried Then
                _logoTried = True
                Try
                    Dim logoPath As String = AppPaths.Asset("logo.png")
                    If File.Exists(logoPath) Then _logo = Image.FromFile(logoPath)
                Catch
                    _logo = Nothing
                End Try
            End If
            Return _logo
        End Get
    End Property

    ''' Window icon — the Chewy Pet mark embedded in ChewyStock.exe.
    Private _icon As Icon
    Public ReadOnly Property AppIcon As Icon
        Get
            If _icon Is Nothing Then
                Try
                    _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                Catch
                End Try
            End If
            Return _icon
        End Get
    End Property

    ''' The company stamp + signature printed on receipts (Assets\signature.png).
    Private _signature As Image
    Private _signatureTried As Boolean
    Public ReadOnly Property Signature As Image
        Get
            If Not _signatureTried Then
                _signatureTried = True
                Try
                    Dim p = AppPaths.Asset("signature.png")
                    If File.Exists(p) Then _signature = Image.FromFile(p)
                Catch
                    _signature = Nothing
                End Try
            End If
            Return _signature
        End Get
    End Property

    ' ===== Palette model =====
    Public Class Palette
        Public Property Name As String
        Public Property WindowBg As Color
        Public Property Surface As Color
        Public Property TextPrimary As Color
        Public Property TextMuted As Color
        Public Property NavBg As Color
        Public Property NavFg As Color
        Public Property Primary As Color        ' accent / call-to-action buttons
        Public Property PrimaryFg As Color
        Public Property Danger As Color         ' sign out / destructive
        Public Property DangerFg As Color
        Public Property GridHeaderBg As Color
        Public Property GridHeaderFg As Color
        Public Property GridAltRowBg As Color
        Public Property GridLineColor As Color
        Public Property GridSelectionBg As Color
        Public Property GridSelectionFg As Color
    End Class

    ''' Built-in palettes. "Soft Mist" (default) is a cool, low-contrast working
    ''' theme; "Chewy Purple" is the full-strength brand version.
    Public ReadOnly Property Palettes As List(Of Palette) = New List(Of Palette) From {
        New Palette With {
            .Name = "Soft Mist",
            .WindowBg = FromHex("#F2F5FA"), .Surface = Color.White,
            .TextPrimary = FromHex("#25313F"), .TextMuted = FromHex("#6E7C8C"),
            .NavBg = FromHex("#E7EDF6"), .NavFg = FromHex("#26374D"),
            .Primary = FromHex("#6D5BD0"), .PrimaryFg = Color.White,
            .Danger = FromHex("#CF6E6E"), .DangerFg = Color.White,
            .GridHeaderBg = FromHex("#E4EBF4"), .GridHeaderFg = FromHex("#2A3A50"),
            .GridAltRowBg = FromHex("#F7F9FC"), .GridLineColor = FromHex("#E2E8F0"),
            .GridSelectionBg = FromHex("#DCE7F7"), .GridSelectionFg = FromHex("#1F2A37")
        },
        New Palette With {
            .Name = "Chewy Purple",
            .WindowBg = FromHex("#F6F3FA"), .Surface = Color.White,
            .TextPrimary = FromHex("#2A1A3E"), .TextMuted = FromHex("#7A6B8C"),
            .NavBg = FromHex("#7A28CC"), .NavFg = Color.White,
            .Primary = FromHex("#F07D18"), .PrimaryFg = Color.White,
            .Danger = FromHex("#D64545"), .DangerFg = Color.White,
            .GridHeaderBg = FromHex("#7A28CC"), .GridHeaderFg = Color.White,
            .GridAltRowBg = FromHex("#F3ECFA"), .GridLineColor = FromHex("#E2D6F0"),
            .GridSelectionBg = FromHex("#E7D3FA"), .GridSelectionFg = FromHex("#2A1A3E")
        },
        New Palette With {
            .Name = "Midnight Blue",
            .WindowBg = FromHex("#F4F6F8"), .Surface = Color.White,
            .TextPrimary = FromHex("#12233B"), .TextMuted = FromHex("#5B6B7B"),
            .NavBg = FromHex("#12233B"), .NavFg = Color.White,
            .Primary = FromHex("#2E7DD1"), .PrimaryFg = Color.White,
            .Danger = FromHex("#C0392B"), .DangerFg = Color.White,
            .GridHeaderBg = FromHex("#12233B"), .GridHeaderFg = Color.White,
            .GridAltRowBg = FromHex("#EEF3F8"), .GridLineColor = FromHex("#D8E0E8"),
            .GridSelectionBg = FromHex("#CFE3F5"), .GridSelectionFg = FromHex("#12233B")
        },
        New Palette With {
            .Name = "Forest",
            .WindowBg = FromHex("#F3F7F3"), .Surface = Color.White,
            .TextPrimary = FromHex("#1E3A2B"), .TextMuted = FromHex("#5F7568"),
            .NavBg = FromHex("#1E5B3E"), .NavFg = Color.White,
            .Primary = FromHex("#E08A1E"), .PrimaryFg = Color.White,
            .Danger = FromHex("#C0392B"), .DangerFg = Color.White,
            .GridHeaderBg = FromHex("#1E5B3E"), .GridHeaderFg = Color.White,
            .GridAltRowBg = FromHex("#EAF2EC"), .GridLineColor = FromHex("#D5E4D9"),
            .GridSelectionBg = FromHex("#CDE7D6"), .GridSelectionFg = FromHex("#1E3A2B")
        },
        New Palette With {
            .Name = "Slate Mono",
            .WindowBg = FromHex("#F4F4F5"), .Surface = Color.White,
            .TextPrimary = FromHex("#1F2937"), .TextMuted = FromHex("#6B7280"),
            .NavBg = FromHex("#374151"), .NavFg = Color.White,
            .Primary = FromHex("#4B5563"), .PrimaryFg = Color.White,
            .Danger = FromHex("#B91C1C"), .DangerFg = Color.White,
            .GridHeaderBg = FromHex("#374151"), .GridHeaderFg = Color.White,
            .GridAltRowBg = FromHex("#F0F0F1"), .GridLineColor = FromHex("#DDDDDF"),
            .GridSelectionBg = FromHex("#D6DAE0"), .GridSelectionFg = FromHex("#1F2937")
        },
        New Palette With {
            .Name = "High Contrast",
            .WindowBg = Color.White, .Surface = Color.White,
            .TextPrimary = Color.Black, .TextMuted = FromHex("#333333"),
            .NavBg = Color.Black, .NavFg = Color.White,
            .Primary = FromHex("#0047AB"), .PrimaryFg = Color.White,
            .Danger = FromHex("#B00020"), .DangerFg = Color.White,
            .GridHeaderBg = Color.Black, .GridHeaderFg = Color.White,
            .GridAltRowBg = FromHex("#EDEDED"), .GridLineColor = Color.Black,
            .GridSelectionBg = FromHex("#FFE08A"), .GridSelectionFg = Color.Black
        }
    }

    ' ===== Table design =====
    Public Enum TableDensity
        Compact = 0
        Comfortable = 1
        Spacious = 2
    End Enum

    Public Enum TableLines
        None = 0
        Horizontal = 1
        Grid = 2
    End Enum

    ' ===== Active settings (mutable) =====
    Public Property Current As Palette = Palettes(0)
    Public Property BaseFontSize As Single = 14.0F
    Public Property Density As TableDensity = TableDensity.Comfortable
    Public Property RowStripes As Boolean = True
    Public Property Lines As TableLines = TableLines.Horizontal
    Public Property BoldHeaders As Boolean = True
    Public Property ShowGridRowNumbers As Boolean = False
    Public Property ReducedEffects As Boolean = False

    ''' Fired after settings change so already-open windows can restyle themselves.
    Public Event ThemeChanged As EventHandler

    Public Sub RaiseThemeChanged()
        RaiseEvent ThemeChanged(Nothing, EventArgs.Empty)
    End Sub

    ' ===== Fonts =====
    Public Function BaseFont() As Font
        Return New Font("Segoe UI", BaseFontSize)
    End Function

    Public Function HeadingFont(Optional scale As Single = 1.4F) As Font
        Return New Font("Segoe UI", BaseFontSize * scale, FontStyle.Bold)
    End Function

    Private Function RowHeight() As Integer
        Select Case Density
            Case TableDensity.Compact : Return CInt(BaseFontSize) + 12
            Case TableDensity.Spacious : Return CInt(BaseFontSize) + 26
            Case Else : Return CInt(BaseFontSize) + 18
        End Select
    End Function

    ' ===== Appliers =====

    ''' Style a DataGridView to the active palette + table design.
    Public Sub ApplyGrid(g As DataGridView)
        If g Is Nothing Then Return
        g.EnableHeadersVisualStyles = False
        g.BackgroundColor = Current.Surface
        g.BorderStyle = BorderStyle.None
        g.Font = BaseFont()
        g.GridColor = Current.GridLineColor
        g.RowHeadersVisible = ShowGridRowNumbers
        g.RowHeadersWidth = If(ShowGridRowNumbers, 52, 41)
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        g.ColumnHeadersHeight = RowHeight() + 6
        g.RowTemplate.Height = RowHeight()

        With g.ColumnHeadersDefaultCellStyle
            .BackColor = Current.GridHeaderBg
            .ForeColor = Current.GridHeaderFg
            .SelectionBackColor = Current.GridHeaderBg
            .SelectionForeColor = Current.GridHeaderFg
            .Font = New Font("Segoe UI", BaseFontSize, If(BoldHeaders, FontStyle.Bold, FontStyle.Regular))
            .Padding = New Padding(8, 0, 8, 0)
            .Alignment = DataGridViewContentAlignment.MiddleLeft
        End With

        With g.DefaultCellStyle
            .BackColor = Current.Surface
            .ForeColor = Current.TextPrimary
            .SelectionBackColor = Current.GridSelectionBg
            .SelectionForeColor = Current.GridSelectionFg
            .Padding = New Padding(8, 2, 8, 2)
            .Font = BaseFont()
        End With

        g.AlternatingRowsDefaultCellStyle.BackColor =
            If(RowStripes, Current.GridAltRowBg, Current.Surface)
        g.AlternatingRowsDefaultCellStyle.SelectionBackColor = Current.GridSelectionBg
        g.AlternatingRowsDefaultCellStyle.SelectionForeColor = Current.GridSelectionFg

        Select Case Lines
            Case TableLines.None : g.CellBorderStyle = DataGridViewCellBorderStyle.None
            Case TableLines.Horizontal : g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
            Case Else : g.CellBorderStyle = DataGridViewCellBorderStyle.Single
        End Select
    End Sub

    ''' Flat "accent" button (primary action).
    Public Sub StylePrimaryButton(b As Button)
        StyleFlat(b, Current.Primary, Current.PrimaryFg)
    End Sub

    ''' Flat "danger" button (sign out / destructive).
    Public Sub StyleDangerButton(b As Button)
        StyleFlat(b, Current.Danger, Current.DangerFg)
    End Sub

    Private Sub StyleFlat(b As Button, back As Color, fore As Color)
        If b Is Nothing Then Return
        b.FlatStyle = FlatStyle.Flat
        b.FlatAppearance.BorderSize = 0
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.15F)
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.05F)
        b.BackColor = back
        b.ForeColor = fore
        b.Font = New Font("Segoe UI", BaseFontSize, FontStyle.Bold)
        b.Padding = New Padding(14, 6, 14, 6)
        b.AutoSize = True
        b.UseVisualStyleBackColor = False
        b.Cursor = Cursors.Hand
    End Sub

    ''' Recursively apply base font + window colours to a control tree.
    ''' Buttons already styled as primary/danger (Tag = "primary"/"danger") and
    ''' DataGridViews are handled separately and skipped here.
    Public Sub Apply(root As Control)
        If root Is Nothing Then Return

        If TypeOf root Is Form Then
            root.BackColor = Current.WindowBg
            root.ForeColor = Current.TextPrimary
            If AppIcon IsNot Nothing Then DirectCast(root, Form).Icon = AppIcon
        End If
        root.Font = BaseFont()

        ApplyRecursive(root)

        ' Translate captions into the active language (no-op for English).
        Try
            Lang.ApplyByText(root)
        Catch
        End Try
    End Sub

    Private Sub ApplyRecursive(parent As Control)
        For Each c As Control In parent.Controls
            ' Controls that manage their own font opt out with Tag = "keepfont".
            If TryCast(c.Tag, String) = "keepfont" Then
                If c.HasChildren Then ApplyRecursive(c)
                Continue For
            End If
            Select Case True
                Case TypeOf c Is DataGridView
                    ApplyGrid(DirectCast(c, DataGridView))
                Case TypeOf c Is Button
                    Dim b = DirectCast(c, Button)
                    Dim kind = TryCast(b.Tag, String)
                    If kind = "primary" Then
                        StylePrimaryButton(b)
                    ElseIf kind = "danger" Then
                        StyleDangerButton(b)
                    Else
                        ' Plain buttons are bold too — flat/system rendering makes regular weight hard to spot.
                        b.Font = New Font(BaseFont(), FontStyle.Bold)
                    End If
                Case TypeOf c Is Label
                    c.Font = If(TryCast(c.Tag, String) = "heading", HeadingFont(), BaseFont())
                Case TypeOf c Is TabControl
                    ' Tab captions (e.g. "Monthly payroll") in bold so they read clearly against the tab strip.
                    c.Font = New Font(BaseFont(), FontStyle.Bold)
                Case Else
                    c.Font = BaseFont()
            End Select
            If c.HasChildren Then ApplyRecursive(c)
        Next
    End Sub

    ' ===== Persistence (AppSettings key/value table) =====

    Public Sub Load()
        EnsureSettingsTable()
        Try
            Dim t = DataAccess.GetTable("SELECT SettingKey, SettingValue FROM AppSettings WHERE SettingKey LIKE 'theme.%'")
            Dim map As New Dictionary(Of String, String)
            For Each r As DataRow In t.Rows
                map(CStr(r("SettingKey"))) = CStr(r("SettingValue"))
            Next
            If map.ContainsKey("theme.palette") Then
                Dim p = Palettes.FirstOrDefault(Function(x) x.Name = map("theme.palette"))
                If p IsNot Nothing Then Current = p
            End If
            ' One-off move to the softer default for databases still on the old
            ' default palette. Anything chosen deliberately afterwards is kept.
            If Current.Name = "Chewy Purple" AndAlso Not map.ContainsKey("theme.softened") Then
                Current = Palettes(0)
                Try
                    SetSetting("theme.palette", Current.Name)
                    SetSetting("theme.softened", "1")
                Catch
                End Try
            End If
            Dim fs As Single
            If map.ContainsKey("theme.fontSize") AndAlso Single.TryParse(map("theme.fontSize"), fs) Then BaseFontSize = Math.Max(9, Math.Min(22, fs))
            Dim d As Integer
            If map.ContainsKey("theme.density") AndAlso Integer.TryParse(map("theme.density"), d) Then Density = CType(d, TableDensity)
            Dim ln As Integer
            If map.ContainsKey("theme.lines") AndAlso Integer.TryParse(map("theme.lines"), ln) Then Lines = CType(ln, TableLines)
            Dim b As Boolean
            If map.ContainsKey("theme.stripes") AndAlso Boolean.TryParse(map("theme.stripes"), b) Then RowStripes = b
            If map.ContainsKey("theme.boldHeaders") AndAlso Boolean.TryParse(map("theme.boldHeaders"), b) Then BoldHeaders = b
            If map.ContainsKey("theme.gridRowNumbers") AndAlso Boolean.TryParse(map("theme.gridRowNumbers"), b) Then ShowGridRowNumbers = b
            If map.ContainsKey("theme.reducedEffects") AndAlso Boolean.TryParse(map("theme.reducedEffects"), b) Then ReducedEffects = b
        Catch
            ' first run / no DB yet — keep defaults
        End Try
    End Sub

    Public Sub Save()
        EnsureSettingsTable()
        SetSetting("theme.palette", Current.Name)
        SetSetting("theme.fontSize", BaseFontSize.ToString())
        SetSetting("theme.density", CInt(Density).ToString())
        SetSetting("theme.lines", CInt(Lines).ToString())
        SetSetting("theme.stripes", RowStripes.ToString())
        SetSetting("theme.boldHeaders", BoldHeaders.ToString())
        SetSetting("theme.gridRowNumbers", ShowGridRowNumbers.ToString())
        SetSetting("theme.reducedEffects", ReducedEffects.ToString())
    End Sub

    Private Sub SetSetting(key As String, value As String)
        DataAccess.Execute(
            "MERGE AppSettings AS t USING (SELECT @k AS k, @v AS v) AS s ON t.SettingKey = s.k " &
            "WHEN MATCHED THEN UPDATE SET SettingValue = s.v " &
            "WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue) VALUES (s.k, s.v);",
            New Dictionary(Of String, Object) From {{"@k", key}, {"@v", value}})
    End Sub

    ''' Create AppSettings if an older database doesn't have it yet.
    Private Sub EnsureSettingsTable()
        Try
            DataAccess.Execute(
                "IF OBJECT_ID('dbo.AppSettings', 'U') IS NULL " &
                "CREATE TABLE AppSettings (SettingKey NVARCHAR(80) NOT NULL PRIMARY KEY, SettingValue NVARCHAR(400) NULL);")
        Catch
            ' no DB connection — Appearance screen will just not persist
        End Try
    End Sub

    ' ===== helpers =====
    Public Function FromHex(hex As String) As Color
        Return ColorTranslator.FromHtml(hex)
    End Function

End Module
