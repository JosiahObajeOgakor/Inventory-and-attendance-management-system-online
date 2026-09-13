Imports System.Windows.Forms
Imports System.IO
Imports System.Text.Json

''' App language: English (the base) and Igbo. The Igbo catalogue below can be
''' corrected or extended at runtime — without rebuilding — by dropping
''' Assets\lang\ig.json next to the app: a flat { "English text": "translation" } map.
'''
''' Two ways to translate:
'''   • Lang.T("English text")            — explicit, for code-built strings.
'''   • Lang.ApplyByText(control)         — walks a control tree and translates any
'''                                         .Text that matches a catalogued English
'''                                         string. Called from Theme.Apply, so
'''                                         every screen is translated on show.
Public Module Lang

    Public ReadOnly Property Available As (Code As String, Name As String)() = {
        ("en", "English"), ("ig", "Igbo")
    }

    Private _code As String = Nothing
    Private _map As Dictionary(Of String, String)

    Public Event LanguageChanged As EventHandler

    Public ReadOnly Property CurrentCode As String
        Get
            If _code Is Nothing Then _code = LoadSaved()
            Return _code
        End Get
    End Property

    ''' Neither language is right-to-left; kept so callers don't have to care.
    Public ReadOnly Property IsRightToLeft As Boolean
        Get
            Return False
        End Get
    End Property

    Public Sub SetLanguage(code As String)
        If Not Catalog.ContainsKey(code) Then code = "en"
        _code = code
        _map = BuildMap(code)
        Try
            DataAccess.Execute(
                "MERGE AppSettings AS t USING (SELECT 'app.lang' AS k) s ON t.SettingKey=s.k " &
                "WHEN MATCHED THEN UPDATE SET SettingValue=@v WHEN NOT MATCHED THEN INSERT (SettingKey,SettingValue) VALUES ('app.lang',@v);",
                New Dictionary(Of String, Object) From {{"@v", code}})
        Catch
        End Try
        RaiseEvent LanguageChanged(Nothing, EventArgs.Empty)
    End Sub

    Private Function LoadSaved() As String
        Try
            Dim t = DataAccess.GetTable("SELECT SettingValue FROM AppSettings WHERE SettingKey='app.lang'")
            If t.Rows.Count > 0 Then
                Dim c = Convert.ToString(t.Rows(0)(0))
                If Catalog.ContainsKey(c) Then Return c
            End If
        Catch
        End Try
        Return "en"
    End Function

    ''' Translate one English string (returns it unchanged if untranslated).
    Public Function T(englishText As String) As String
        If String.IsNullOrEmpty(englishText) OrElse CurrentCode = "en" Then Return englishText
        EnsureMap()
        Dim v As String = Nothing
        Return If(_map.TryGetValue(englishText, v), v, englishText)
    End Function

    Private Sub EnsureMap()
        If _map Is Nothing Then _map = BuildMap(CurrentCode)
    End Sub

    ''' Translation → English, across every catalogue. Switching back to English
    ''' (or between languages) uses this to put captions back before translating
    ''' again, so a screen never gets stuck in the previous language.
    Private _toEnglish As Dictionary(Of String, String)

    Private Function ToEnglish() As Dictionary(Of String, String)
        If _toEnglish IsNot Nothing Then Return _toEnglish
        Dim d As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each language In Catalog
            If language.Key = "en" Then Continue For
            For Each kv In BuildMap(language.Key)
                If Not d.ContainsKey(kv.Value) Then d(kv.Value) = kv.Key
            Next
        Next
        _toEnglish = d
        Return d
    End Function

    ''' The English caption behind whatever a control currently shows.
    Private Function EnglishOf(text As String) As String
        Dim english As String = Nothing
        Return If(ToEnglish().TryGetValue(text, english), english, text)
    End Function

    Private Function BuildMap(code As String) As Dictionary(Of String, String)
        Dim d As New Dictionary(Of String, String)(StringComparer.Ordinal)
        If Catalog.ContainsKey(code) Then
            For Each kv In Catalog(code)
                d(kv.Key) = kv.Value
            Next
        End If
        ' Optional external override file.
        Try
            Dim jsonPath As String = AppPaths.Asset(Path.Combine("lang", code & ".json"))
            If File.Exists(jsonPath) Then
                Dim ext = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(File.ReadAllText(jsonPath))
                If ext IsNot Nothing Then
                    For Each kv In ext
                        d(kv.Key) = kv.Value
                    Next
                End If
            End If
        Catch
        End Try
        Return d
    End Function

    ''' Walk a control tree and translate any caption we have Igbo for.
    Public Sub ApplyByText(root As Control)
        If root Is Nothing Then Return
        EnsureMap()
        TranslateTree(root)
    End Sub

    ''' Every caption goes back to English first, then into the current language —
    ''' so switching Igbo → English (or back again) always lands correctly.
    Private Function Caption(current As String) As String
        If String.IsNullOrEmpty(current) Then Return current
        Dim english = EnglishOf(current)
        If CurrentCode = "en" Then Return english
        Dim translated As String = Nothing
        Return If(_map.TryGetValue(english, translated), translated, english)
    End Function

    Private Sub TranslateTree(parent As Control)
        For Each c As Control In parent.Controls
            c.Text = Caption(c.Text)
            If TypeOf c Is ToolStrip Then
                For Each it As ToolStripItem In DirectCast(c, ToolStrip).Items
                    it.Text = Caption(it.Text)
                Next
            End If
            If c.HasChildren Then TranslateTree(c)
        Next
    End Sub

    Private Sub ApplyRtl(f As Control, rtl As Boolean)
        Try
            f.RightToLeft = If(rtl, RightToLeft.Yes, RightToLeft.No)
            If TypeOf f Is Form Then DirectCast(f, Form).RightToLeftLayout = rtl
        Catch
        End Try
    End Sub

    ' =======================================================================
    '  Translation catalog.  Keys are the exact English text shown in the UI.
    '  Machine-assisted Igbo — worth a native speaker's review before rollout.
    '  Extend or correct without recompiling via Assets\lang\<code>.json.
    ' =======================================================================
    Private ReadOnly Catalog As New Dictionary(Of String, Dictionary(Of String, String)) From {
        {"en", New Dictionary(Of String, String)()},
        {"ig", Ig()}
    }

    Private Function Ig() As Dictionary(Of String, String)
        Return New Dictionary(Of String, String) From {
            {"Dashboard", "Nchịkọta"}, {"Inventory", "Ngwongwo"}, {"Sales", "Ire ahịa"},
            {"Receipts", "Akwụkwọ nnata"}, {"Purchases", "Ịzụ ahịa"}, {"Suppliers", "Ndị na-eweta ngwongwo"},
            {"Customers", "Ndị ahịa"}, {"Rebates", "Ego mgbaghara"}, {"Waybill", "Akwụkwọ mbupu"},
            {"Income", "Ego mbata"}, {"Finance", "Ego"}, {"Expenses", "Mmefu ego"},
            {"Employees", "Ndị ọrụ"}, {"Insights", "Nyocha"}, {"AI Assistant", "Onye enyemaka AI"},
            {"Appearance", "Ọdịdị"}, {"Sign out", "Pụọ"}, {"Activate", "Kwado"},
            {"Activate now", "Kwado ugbu a"}, {"Change my password…", "Gbanwee okwuntughe m…"},
            {"Add user account…", "Tinye akaụntụ onye ọrụ…"},
            {"Backup database…", "Chekwaa ngwaebe data…"},
            {"Export all data (Excel)…", "Bupụta data niile (Excel)…"},
            {"Database storage and archive…", "Nchekwa data na nchekwa ochie…"},
            {"Installation / license info…", "Ozi ntinye / ikike…"},
            {"Sign in", "Banye"}, {"Username", "Aha njirimara"}, {"Password", "Okwuntughe"},
            {"Confirm password", "Kwenye okwuntughe"}, {"Confirm new", "Kwenye nke ọhụrụ"},
            {"New password", "Okwuntughe ọhụrụ"}, {"Current password", "Okwuntughe ugbu a"},
            {"Test database connection", "Nwalee njikọ ngwaebe data"},
            {"Save password", "Chekwaa okwuntughe"}, {"Full name", "Aha zuru ezu"},
            {"Role", "Ọrụ"}, {"Create account", "Mepụta akaụntụ"},
            {"Create administrator", "Mepụta onye nlekọta"},
            {"ChewyStock is not activated", "Akwadobeghị ChewyStock"},
            {"Activate this computer", "Kwado kọmpụta a"},
            {"Pay now  —  card · bank transfer · USSD", "Kwụọ ugbu a  —  kaadị · ịnyefe ego · USSD"},
            {"I've already paid — activate now", "Akwụọlarị m — kwado ugbu a"},
            {"Have an installation code or offline key?", "Ị nwere koodu ntinye ma ọ bụ igodo ntinye?"},
            {"Skip for now", "Hapụ ugbu a"}, {"Quit", "Kwụsị"}, {"Copy", "Detuo"},
            {"Machine ID", "ID igwe"}, {"Copy Machine ID", "Detuo ID igwe"},
            {"Email", "Ozi-e"}, {"Country", "Obodo"},
            {"Save", "Chekwaa"}, {"Cancel", "Kagbuo"}, {"OK", "Ọ dị mma"}, {"Delete", "Hichapụ"},
            {"Delete item", "Hichapụ ngwaahịa"}, {"Delete invoice", "Hichapụ akwụkwọ ụgwọ"},
            {"Delete supplier", "Hichapụ onye na-eweta ngwongwo"}, {"Delete entry", "Hichapụ ndenye"},
            {"Delete employee", "Hichapụ onye ọrụ"}, {"Delete loan", "Hichapụ mbinye ego"},
            {"Remove from month", "Wepụ n'ọnwa a"}, {"Edit", "Dezie"},
            {"Edit prices", "Dezie ọnụahịa"}, {"Export CSV", "Bupụta CSV"},
            {"Export Excel", "Bupụta Excel"}, {"Export all finance (Excel)", "Bupụta ego niile (Excel)"},
            {"Search:", "Chọọ:"}, {"Rows per page:", "Ahịrị n'ibe ọ bụla:"},
            {"+ Add item", "+ Tinye ngwaahịa"}, {"+ Add customer", "+ Tinye onye ahịa"},
            {"+ Add supplier", "+ Tinye onye na-eweta ngwongwo"}, {"+ Add expense", "+ Tinye mmefu ego"},
            {"+ Add employee", "+ Tinye onye ọrụ"}, {"+ New invoice", "+ Akwụkwọ ụgwọ ọhụrụ"},
            {"+ New sale", "+ Ire ọhụrụ"}, {"+ New purchase order", "+ Iwu ịzụ ahịa ọhụrụ"},
            {"+ Record production", "+ Dekọọ ihe e mepụtara"},
            {"Record production", "Dekọọ ihe e mepụtara"},
            {"Production history", "Akụkọ ihe e mepụtara"},
            {"Produced on", "E mepụtara na"}, {"Quantity produced", "Ọnụọgụ e mepụtara"},
            {"Into warehouse", "Banye n'ụlọ nkwakọba"}, {"Add to inventory", "Tinye na ngwongwo"},
            {"New sale", "Ire ọhụrụ"}, {"Save sale", "Chekwaa ire"},
            {"Sale date:", "Ụbọchị ire:"}, {"Purchase date:", "Ụbọchị ịzụta:"},
            {"Record payment", "Dekọọ ịkwụ ụgwọ"}, {"View metrics", "Lee ọnụọgụgụ"},
            {"View receipt / PDF", "Lee akwụkwọ nnata / PDF"}, {"Print…", "Bipụta…"},
            {"Save as PDF…", "Chekwaa dị ka PDF…"}, {"Fit width", "Mee ka obosara dabaa"},
            {"Save / share report (PDF / Excel)", "Chekwaa / kesaa akụkọ (PDF / Excel)"},
            {"Low stock alerts", "Ọkwa ngwongwo na-agwụ"}, {"Recent invoices", "Akwụkwọ ụgwọ ọhụrụ"},
            {"Operating expenses", "Mmefu ego ọrụ"}, {"Credit and debit ledger", "Akwụkwọ ego enyere na nke ji"},
            {"Total stock value", "Uru ngwongwo niile"}, {"Low stock items", "Ngwongwo na-agwụ agwụ"},
            {"Today's invoices", "Akwụkwọ ụgwọ taa"}, {"Revenue (month)", "Ego batara (ọnwa)"},
            {"COGS (month)", "Ọnụahịa ngwongwo erere (ọnwa)"}, {"Gross profit", "Uru mbụ"},
            {"Expenses (month)", "Mmefu ego (ọnwa)"}, {"Net profit (month)", "Uru zuru ezu (ọnwa)"},
            {"Owed to suppliers (AP)", "Ụgwọ anyị ji ndị na-eweta"}, {"Owed to us (AR)", "Ụgwọ a ji anyị"},
            {"Gross sales", "Ire ahịa niile"}, {"VAT collected", "VAT anakọtara"},
            {"Net sales (excl VAT)", "Ire ahịa (VAT esoghị)"}, {"Cash collected", "Ego anatara"},
            {"Still outstanding", "Ka ji ụgwọ"}, {"Cost of goods sold", "Ọnụahịa ngwongwo erere"},
            {"Estimated net profit", "Uru a tụrụ anya"},
            {"Monthly payroll", "Ụgwọ ọnwa"}, {"Staff & loans", "Ndị ọrụ na mbinye ego"},
            {"Generate month", "Mepụta ọnwa"}, {"Mark month paid", "Kwuo na akwụọla ọnwa"},
            {"Give loan", "Nye mbinye ego"}, {"Record repayment", "Dekọọ nkwụghachi"},
            {"Price tier", "Ọkwa ọnụahịa"}, {"Distributor", "Onye nkesa"}, {"Wholesaler", "Onye na-ere ọtụtụ"},
            {"Retailer", "Onye na-ere nta"}, {"Walk-in", "Onye bịara ọbịbịa"}, {"Ranking", "Ọkwa"},
            {"Payment method", "Ụzọ ịkwụ ụgwọ"}, {"Amount paid now", "Ego akwụrụ ugbu a"},
            {"Balance due date", "Ụbọchị ịkwụ ụgwọ fọdụrụ"}, {"Subtotal", "Mkpọkọta nta"},
            {"Discount", "Mbelata ọnụahịa"}, {"Add VAT (7.5%)", "Tinye VAT (7.5%)"},
            {"VAT", "VAT"}, {"TOTAL", "MKPỌKỌTA"}, {"Outstanding", "Ụgwọ fọdụrụ"},
            {"Ask", "Jụọ"}, {"Check again", "Lelee ọzọ"},
            {"Download Ollama", "Budata Ollama"}, {"Download the AI model now", "Budata ụdị AI ugbu a"},
            {"Please confirm", "Biko kwenye"}, {"Yes, continue", "Ee, gaa n'ihu"}, {"Notice", "Ọkwa"},
            {"Warehouse", "Ụlọ nkwakọba"}, {"Product", "Ngwaahịa"}, {"Customer", "Onye ahịa"},
            {"Quantity", "Ọnụọgụ"}, {"Batch / lot no.", "Nọmba ogbe"}, {"Expiry", "Ụbọchị ngwụcha"},
            {"Driver name", "Aha onye ọkwọ ụgbọala"}, {"Vehicle plate no.", "Nọmba ụgbọala"},
            {"Destination", "Ebe a na-eje"}, {"Notes", "Ndetu"}, {"Create waybill", "Mepụta akwụkwọ mbupu"}
        }
    End Function

End Module
