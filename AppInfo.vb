Imports System.Configuration

''' Company identity + money/period formatting shared by receipts, waybills and
''' reports. Values come from App.config's appSettings — edit that file with the
''' real address / phone / bank details before printing customer-facing documents.
Public Module AppInfo

    Private Function Cfg(key As String, fallback As String) As String
        Dim v = ConfigurationManager.AppSettings(key)
        ' Unfilled "[placeholder]" values are treated as blank, so they never print.
        If String.IsNullOrWhiteSpace(v) OrElse v.TrimStart().StartsWith("[") Then Return fallback
        Return v
    End Function

    ''' Identity of the business signed into — see Company. ChewyPets reads the
    ''' plain keys (CompanyName, BankName…); Candid Purrfect reads the same keys
    ''' prefixed "Candid" (CandidCompanyAddress, CandidBankName…).
    Public ReadOnly Property CompanyName As String
        Get
            Return Company.Current.DocumentName
        End Get
    End Property

    Public ReadOnly Property CompanyAddress As String
        Get
            Return Company.Current.Setting("CompanyAddress", "")
        End Get
    End Property
    Public ReadOnly Property CompanyPhone As String
        Get
            Return Company.Current.Setting("CompanyPhone", "")
        End Get
    End Property
    Public ReadOnly Property CompanyEmail As String
        Get
            Return Company.Current.Setting("CompanyEmail", "")
        End Get
    End Property
    Public ReadOnly Property CompanyTaxID As String
        Get
            Return Company.Current.Setting("CompanyTaxID", "")
        End Get
    End Property

    ''' Bank accounts printed on receipts: BankName/BankAccountName/BankAccountNumber,
    ''' then Bank2…, Bank3… — for the business signed into. Blank entries are skipped.
    Public Function BankAccounts() As List(Of (Bank As String, AccountName As String, AccountNumber As String))
        Return Company.Current.BankAccounts()
    End Function

    Public ReadOnly Property CurrencySymbol As String
        Get
            Return Cfg("CurrencySymbol", "₦")
        End Get
    End Property

    Public ReadOnly Property VatRate As Decimal
        Get
            Dim r As Decimal
            Return If(Decimal.TryParse(Cfg("VatRate", "7.5"), r), r, 7.5D)
        End Get
    End Property

    ''' Default rebate rate (%). AppSettings 'rebate.ratePct' wins over App.config.
    Public ReadOnly Property RebateRatePct As Decimal
        Get
            Dim r As Decimal
            Try
                Dim t = DataAccess.GetTable("SELECT SettingValue FROM AppSettings WHERE SettingKey = 'rebate.ratePct'")
                If t.Rows.Count > 0 AndAlso Decimal.TryParse(Convert.ToString(t.Rows(0)(0)), r) Then Return r
            Catch
            End Try
            Return If(Decimal.TryParse(Cfg("RebateRatePct", "1.0"), r), r, 1.0D)
        End Get
    End Property

    ''' "₦1,234,500" — no decimals, matches the rest of the app.
    Public Function Money(value As Object) As String
        If value Is Nothing OrElse value Is DBNull.Value Then Return CurrencySymbol & "0"
        Return CurrencySymbol & Convert.ToDecimal(value).ToString("N0")
    End Function

    ''' "₦1,234,500.00" — two decimals, for receipts and other money documents.
    Public Function Money2(value As Object) As String
        If value Is Nothing OrElse value Is DBNull.Value Then Return CurrencySymbol & "0.00"
        Return CurrencySymbol & Convert.ToDecimal(value).ToString("N2")
    End Function

    Public Function MonthName(m As Integer) As String
        If m < 1 OrElse m > 12 Then Return m.ToString()
        Return Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(m)
    End Function

    ''' Period label for a report: "September 2026" or "Full year 2026".
    Public Function PeriodLabel(year As Integer, month As Integer?) As String
        Return If(month.HasValue, $"{MonthName(month.Value)} {year}", $"Full year {year}")
    End Function

End Module
