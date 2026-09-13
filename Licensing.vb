Imports System.Data
Imports System.Security.Cryptography
Imports System.Text
Imports System.Management
Imports System.Configuration
Imports System.Net
Imports System.Text.Json
Imports Microsoft.Win32
Imports System.Windows.Forms

''' Installation licensing. Until this machine holds a valid key, every business
''' module is locked (see frmMain) and each session ends after 10 minutes.
'''
''' A key is an ECDSA P-256 signature (IEEE-P1363, base64url) over
''' "<machineId>|<company>", produced by the licensing service (which holds the
''' PRIVATE key). This app embeds only the PUBLIC key, so it can VERIFY a key
''' OFFLINE forever but can NEVER forge one — there is no shared secret.
'''
''' Activation is automatic: the customer pays $250 via AlatPay on
''' stockdesk-licensing.onrender.com; the service binds the payment to their machine id
''' and the app fetches the key from GET /api/activate. The key is cached in the
''' database AND the registry so it survives an uninstall/reinstall.
Public Module Licensing

    ' ---- Embedded PUBLIC key (P-256 point, base64). Regenerate BOTH this and the
    '      service's EC_PRIVATE_KEY together (licensing-api: npm run keygen). ----
    Private Const PubKeyX As String = "uSledcmeYZGYUao+8BHkqBy9fJ5/4+kDOzb5paC/nVU="
    Private Const PubKeyY As String = "PVwUB5bT23D8KsN8Byb6zqhjLtrBrucu1Bay/cJzk5E="

    Public ReadOnly Property VendorName As String = "Josiah Obaje"
    Public ReadOnly Property VendorEmail As String = "Josiahobaje.dev@gmail.com"
    Private Const RegPath As String = "SOFTWARE\SpringupAI\StockDesk"

    Public ReadOnly Property ApiBaseUrl As String
        Get
            Dim v = ConfigurationManager.AppSettings("LicenseApiUrl")
            Return If(String.IsNullOrWhiteSpace(v), "https://stockdesk-licensing.onrender.com", v.TrimEnd("/"c))
        End Get
    End Property

    ' ===== Machine fingerprint =====

    Private _machineId As String

    ''' Stable id for this PC. Built from things that DON'T change across a
    ''' reinstall, a reboot, or switching WiFi/Ethernet/VPN:
    '''   • Windows install GUID (HKLM\...\Cryptography\MachineGuid)
    '''   • motherboard + BIOS serials (WMI)
    '''   • the machine name
    ''' Hashed, so it leaks nothing. Only a Windows reinstall or a motherboard
    ''' swap changes it — and then the customer just does a one-click transfer.
    Public ReadOnly Property MachineId As String
        Get
            If _machineId IsNot Nothing Then Return _machineId
            Dim raw As New StringBuilder()
            raw.Append(MachineGuid())
            raw.Append("|").Append(WmiValue("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"))
            raw.Append("|").Append(WmiValue("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"))
            raw.Append("|").Append(Environment.MachineName.ToUpperInvariant())
            Using sha = SHA256.Create()
                Dim h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToString()))
                _machineId = Base32(h).Substring(0, 16)
            End Using
            Return _machineId
        End Get
    End Property

    Private Function MachineGuid() As String
        For Each view In {RegistryView.Registry64, RegistryView.Registry32}
            Try
                Using base = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
                    Using k = base.OpenSubKey("SOFTWARE\Microsoft\Cryptography")
                        Dim g = Convert.ToString(k?.GetValue("MachineGuid", ""))
                        If Not String.IsNullOrWhiteSpace(g) Then Return g
                    End Using
                End Using
            Catch
            End Try
        Next
        Return ""
    End Function

    Private Function WmiValue(query As String, prop As String) As String
        Try
            Using searcher As New ManagementObjectSearcher(query)
                For Each o As ManagementObject In searcher.Get()
                    Dim v = Convert.ToString(o(prop))
                    If Not String.IsNullOrWhiteSpace(v) AndAlso v.Trim() <> "0" _
                       AndAlso Not v.Trim().StartsWith("To Be Filled", StringComparison.OrdinalIgnoreCase) Then
                        Return v.Trim()
                    End If
                Next
            End Using
        Catch
        End Try
        Return ""
    End Function

    Private Function CompanyName() As String
        Return If(String.IsNullOrWhiteSpace(ConfigurationManager.AppSettings("CompanyName")), "ChewyPetsFeed", ConfigurationManager.AppSettings("CompanyName"))
    End Function

    ' ===== Key verification (public key only — cannot forge) =====

    ''' True if `key` is a valid signature for THIS machine (or `forMachine`).
    Public Function IsKeyValid(key As String, Optional forMachine As String = Nothing) As Boolean
        If String.IsNullOrWhiteSpace(key) Then Return False
        Dim mid As String = If(String.IsNullOrEmpty(forMachine), MachineId, forMachine)
        Try
            Dim sig = FromBase64Url(key.Trim())
            If sig.Length <> 64 Then Return False
            Dim msg = Encoding.UTF8.GetBytes(mid.Trim().ToUpperInvariant() & "|" & CompanyName().Trim().ToUpperInvariant())
            Dim p As New ECParameters()
            p.Curve = ECCurve.NamedCurves.nistP256
            p.Q = New ECPoint With {
                .X = Convert.FromBase64String(PubKeyX),
                .Y = Convert.FromBase64String(PubKeyY)}
            Using ec = ECDsa.Create()
                ec.ImportParameters(p)
                Return ec.VerifyData(msg, sig, HashAlgorithmName.SHA256)
            End Using
        Catch
            Return False
        End Try
    End Function

    Private Function FromBase64Url(s As String) As Byte()
        s = s.Replace("-"c, "+"c).Replace("_"c, "/"c)
        Select Case s.Length Mod 4
            Case 2 : s &= "=="
            Case 3 : s &= "="
        End Select
        Return Convert.FromBase64String(s)
    End Function

    ' ===== Activation state — cached in the DB AND the registry =====

    ''' Activated = a stored key still validates for this machine. Checked
    ''' offline; the internet is only needed to first obtain the key.
    Public Function IsActivated() As Boolean
#If DEBUG Then
        ' Dev-only bypass: honoured in Debug builds only, so a customer can't
        ' unlock a shipped Release copy by editing StockDesk.exe.config.
        If String.Equals(ConfigurationManager.AppSettings("SkipActivation"), "true", StringComparison.OrdinalIgnoreCase) Then Return True
#End If

        Dim regKey = RegRead("Key")
        If regKey <> "" AndAlso IsKeyValid(regKey) Then
            EnsureDbActivation(regKey)   ' heal the DB copy if it was wiped
            Return True
        End If

        Try
            Dim t = DataAccess.GetTable("SELECT SettingValue FROM AppSettings WHERE SettingKey = 'license.key'")
            If t.Rows.Count > 0 Then
                Dim dbKey = Convert.ToString(t.Rows(0)(0))
                If dbKey <> "" AndAlso IsKeyValid(dbKey) Then
                    RegWrite("Key", dbKey)   ' heal the registry copy
                    Return True
                End If
            End If
        Catch
        End Try
        Return False
    End Function

    ''' Offline activation with a key the vendor generated for THIS machine's id
    ''' (licensing-api: npm run offlinekey -- MACHINEID). No network call. A key
    ''' made for any other PC fails signature verification here.
    Public Function ActivateWithOfflineKey(key As String) As Boolean
        Dim k = If(key, "").Trim()
        If Not IsKeyValid(k) Then Return False
        SaveActivation(k)
        Return True
    End Function

    Public Sub SaveActivation(key As String, Optional email As String = Nothing)
        Dim k = key.Trim()
        RegWrite("Key", k)
        RegWrite("MachineId", MachineId)
        If email IsNot Nothing Then RegWrite("Email", email)
        EnsureDbActivation(k)
    End Sub

    Public Function SavedEmail() As String
        Return RegRead("Email")
    End Function

    Private Sub EnsureDbActivation(norm As String)
        Try
            SetSetting("license.status", "active")
            SetSetting("license.key", norm)
            SetSetting("license.machine", MachineId)
            SetSetting("license.activatedAt", DateTime.UtcNow.ToString("o"))
        Catch
        End Try
    End Sub

    Private Sub SetSetting(k As String, v As String)
        DataAccess.Execute(
            "MERGE AppSettings AS t USING (SELECT @k AS k) s ON t.SettingKey = s.k " &
            "WHEN MATCHED THEN UPDATE SET SettingValue = @v " &
            "WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue) VALUES (@k, @v);",
            New Dictionary(Of String, Object) From {{"@k", k}, {"@v", v}})
    End Sub

    Private Function RegRead(name As String) As String
        Try
            Using k = Registry.CurrentUser.OpenSubKey(RegPath)
                Return If(k Is Nothing, "", Convert.ToString(k.GetValue(name, "")))
            End Using
        Catch
            Return ""
        End Try
    End Function

    Private Sub RegWrite(name As String, value As String)
        Try
            Using k = Registry.CurrentUser.CreateSubKey(RegPath)
                k.SetValue(name, value)
            End Using
        Catch
        End Try
    End Sub

    ' ===== Online activation (stockdesk-licensing.onrender.com) =====

    Public Enum ActivationStatus
        Activated
        Pending          ' payment not seen yet
        TransferRequired ' paid, but the licence is bound to another machine
        TransferPending  ' transfer requested, awaiting approval
        NoInternet
        Failed
    End Enum

    Public Class ActivationResult
        Public Property Status As ActivationStatus
        Public Property Message As String
        Public Property Key As String
    End Class

    Public Class PayInstructions
        Public Property Ok As Boolean
        Public Property AlreadyPaid As Boolean
        Public Property AccountNumber As String
        Public Property BankName As String
        Public Property AccountName As String
        Public Property Amount As String
        Public Property Currency As String
        Public Property Reference As String
        Public Property PayUrl As String
        Public Property ExpiresAt As String
        Public Property ErrorMessage As String
    End Class

    ''' Ask the service to open a payment reference; payment itself happens via
    ''' the AlatPay web-plugin popup on the returned /pay page (card, bank transfer, USSD).
    Public Function StartPurchase(email As String, countryIso2 As String) As PayInstructions
        Dim r As New PayInstructions()
        Try
            Dim payload = JsonSerializer.Serialize(New With {
                .email = email.Trim().ToLowerInvariant(), .machineId = MachineId,
                .company = CompanyName(), .country = countryIso2})
            Dim resp = HttpPost("/api/paylink", payload)
            Using doc = JsonDocument.Parse(resp)
                Dim root = doc.RootElement
                Dim el As JsonElement
                If root.TryGetProperty("alreadyPaid", el) AndAlso el.GetBoolean() Then
                    r.AlreadyPaid = True
                    Return r
                End If
                If root.TryGetProperty("error", el) Then
                    r.ErrorMessage = el.GetString()
                    Return r
                End If
                r.AccountNumber = GetStr(root, "accountNumber")
                r.BankName = GetStr(root, "bankName")
                r.AccountName = GetStr(root, "accountName")
                r.Currency = GetStr(root, "currency")
                r.Reference = GetStr(root, "reference")
                r.PayUrl = GetStr(root, "payUrl")
                r.ExpiresAt = GetStr(root, "expiresAt")
                Dim amtEl As JsonElement
                If root.TryGetProperty("amount", amtEl) Then r.Amount = amtEl.ToString()
                r.Ok = Not String.IsNullOrEmpty(r.Reference)
                If Not r.Ok Then r.ErrorMessage = "The service did not return a payment reference."
            End Using
        Catch ex As WebException
            Dim body = BodyOf(ex)
            If body <> "" AndAlso body.TrimStart().StartsWith("{") Then
                Try
                    Using d = JsonDocument.Parse(body)
                        Dim el As JsonElement
                        If d.RootElement.TryGetProperty("error", el) Then
                            r.ErrorMessage = el.GetString()
                            Return r
                        End If
                    End Using
                Catch
                End Try
            End If
            r.ErrorMessage = Reason(ex)
        Catch ex As Exception
            r.ErrorMessage = ex.Message
        End Try
        Return r
    End Function

    Private Function GetStr(root As JsonElement, name As String) As String
        Dim el As JsonElement
        If root.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.String Then Return el.GetString()
        Return ""
    End Function

    ''' Poll for the key. Call after the customer says they've paid.
    Public Function ActivateOnline(email As String) As ActivationResult
        Dim r As New ActivationResult()
        Try
            Dim q = $"/api/activate?machineId={Uri.EscapeDataString(MachineId)}&email={Uri.EscapeDataString(email.Trim().ToLowerInvariant())}"
            Dim resp = HttpGet(q)
            ParseActivation(resp, email, r)
        Catch ex As WebException
            Dim body = BodyOf(ex)
            If body <> "" AndAlso body.TrimStart().StartsWith("{") Then
                Try
                    ParseActivation(body, email, r)
                    Return r
                Catch
                End Try
            End If
            r.Status = ActivationStatus.NoInternet
            r.Message = Reason(ex)
        Catch ex As Exception
            r.Status = ActivationStatus.Failed
            r.Message = ex.Message
        End Try
        Return r
    End Function

    Public Function RequestTransfer(email As String) As ActivationResult
        Dim r As New ActivationResult()
        Try
            Dim payload = JsonSerializer.Serialize(New With {.email = email.Trim().ToLowerInvariant(), .newMachineId = MachineId})
            Dim resp = HttpPost("/api/transfer", payload)
            ParseActivation(resp, email, r)
        Catch ex As WebException
            r.Status = ActivationStatus.NoInternet
            r.Message = Reason(ex)
        Catch ex As Exception
            r.Status = ActivationStatus.Failed
            r.Message = ex.Message
        End Try
        Return r
    End Function

    ''' Redeem a one-time installation code (issued only by the vendor — see
    ''' LICENSING.md) instead of paying. Activates THIS machine; the code is
    ''' burned on the server the moment it's accepted, so it can never be reused.
    Public Function ActivateWithCode(email As String, code As String) As ActivationResult
        Dim r As New ActivationResult()
        Try
            Dim payload = JsonSerializer.Serialize(New With {
                .email = email.Trim().ToLowerInvariant(), .machineId = MachineId,
                .company = CompanyName(), .code = code.Trim().ToUpperInvariant()})
            Dim resp = HttpPost("/api/activate-code", payload)
            ParseActivation(resp, email, r)
        Catch ex As WebException
            Dim body = BodyOf(ex)
            If body <> "" AndAlso body.TrimStart().StartsWith("{") Then
                Try
                    ParseActivation(body, email, r)
                    Return r
                Catch
                End Try
            End If
            r.Status = ActivationStatus.NoInternet
            r.Message = Reason(ex)
        Catch ex As Exception
            r.Status = ActivationStatus.Failed
            r.Message = ex.Message
        End Try
        Return r
    End Function

    Private Sub ParseActivation(resp As String, email As String, r As ActivationResult)
        Using doc = JsonDocument.Parse(resp)
            Dim root = doc.RootElement
            Dim status = ""
            Dim el As JsonElement
            If root.TryGetProperty("status", el) Then status = el.GetString()
            If root.TryGetProperty("message", el) Then r.Message = el.GetString()
            Select Case status
                Case "active"
                    r.Key = root.GetProperty("key").GetString()
                    If IsKeyValid(r.Key) Then
                        SaveActivation(r.Key, email)
                        r.Status = ActivationStatus.Activated
                        r.Message = "Activated. Every module is unlocked — permanently."
                    Else
                        r.Status = ActivationStatus.Failed
                        r.Message = "The service returned a key that doesn't match this machine. Contact support."
                    End If
                Case "pending" : r.Status = ActivationStatus.Pending
                Case "transfer_required" : r.Status = ActivationStatus.TransferRequired
                Case "transfer_pending" : r.Status = ActivationStatus.TransferPending
                Case Else : r.Status = ActivationStatus.Failed
            End Select
        End Using
    End Sub

    Private Function HttpGet(path As String) As String
        Using wc = NewClient()
            Return wc.DownloadString(ApiBaseUrl & path)
        End Using
    End Function

    Private Function HttpPost(path As String, jsonBody As String) As String
        Using wc = NewClient()
            wc.Headers(HttpRequestHeader.ContentType) = "application/json"
            Return wc.UploadString(ApiBaseUrl & path, jsonBody)
        End Using
    End Function

    Private Function NewClient() As WebClient
        Try
            Net.ServicePointManager.SecurityProtocol =
                Net.SecurityProtocolType.Tls12 Or Net.SecurityProtocolType.Tls11
        Catch
        End Try
        Dim wc As New WebClient() With {.Encoding = Encoding.UTF8}
        Return wc
    End Function

    ''' If the licensing service replied with an error status, return its JSON
    ''' body so the caller can still read {status/error/message}. Otherwise "".
    Private Function BodyOf(ex As WebException) As String
        Try
            If ex.Response IsNot Nothing Then
                Using rs = ex.Response.GetResponseStream()
                    Using sr As New IO.StreamReader(rs)
                        Return sr.ReadToEnd()
                    End Using
                End Using
            End If
        Catch
        End Try
        Return ""
    End Function

    ''' Plain-English reason a web call failed.
    Private Function Reason(ex As WebException) As String
        Select Case ex.Status
            Case WebExceptionStatus.NameResolutionFailure
                Return "No internet connection (can't look up " & ApiBaseUrl & ")."
            Case WebExceptionStatus.ConnectFailure, WebExceptionStatus.Timeout
                Return "Can't reach " & ApiBaseUrl & " — check your internet, then try again."
            Case WebExceptionStatus.TrustFailure, WebExceptionStatus.SecureChannelFailure
                Return "Secure connection to " & ApiBaseUrl & " failed."
            Case WebExceptionStatus.ProtocolError
                Dim code = 0
                Try
                    code = CInt(DirectCast(ex.Response, HttpWebResponse).StatusCode)
                Catch
                End Try
                If code = 404 OrElse code = 502 OrElse code = 503 Then
                    Return "The licensing service isn't running yet at " & ApiBaseUrl & ". (Deploy it, or wait ~40s if it was asleep and retry.)"
                End If
                Return "The licensing service returned an error (" & code & "). Try again shortly."
            Case Else
                Return "Couldn't contact the licensing service. " & ex.Message
        End Select
    End Function

    ''' Open the marketing / buy page in the browser, pre-filled.
    Public Sub OpenBuyPage(email As String, countryIso2 As String)
        Try
            Dim url = ApiBaseUrl & "/?email=" & Uri.EscapeDataString(email) &
                      "&machineId=" & Uri.EscapeDataString(MachineId) &
                      "&country=" & Uri.EscapeDataString(countryIso2)
            Process.Start(url)
        Catch
        End Try
    End Sub

    ' ===== base32 (Crockford-ish, no padding) =====
    Private Const Alphabet As String = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

    Private Function Base32(data As Byte()) As String
        Dim sb As New StringBuilder()
        Dim buffer As Integer = 0, bits As Integer = 0
        For Each b In data
            buffer = (buffer << 8) Or b
            bits += 8
            While bits >= 5
                sb.Append(Alphabet((buffer >> (bits - 5)) And 31))
                bits -= 5
            End While
        Next
        If bits > 0 Then sb.Append(Alphabet((buffer << (5 - bits)) And 31))
        Return sb.ToString()
    End Function

End Module
