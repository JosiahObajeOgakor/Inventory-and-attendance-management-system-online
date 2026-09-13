Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Configuration
Imports System.Threading.Tasks

''' Thin client for a local Ollama server. Everything runs on this PC — the only
''' network involved is the loopback address, so no internet is needed once the
''' model has been pulled. URL + model come from App.config (OllamaUrl /
''' OllamaModel). Ollama (or LM Studio, or any OpenAI-ish local runner) must be
''' running; this class only talks to it.
Public Class OllamaClient

    Private ReadOnly http As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(180)}

    Public ReadOnly Property BaseUrl As String
    Public Property Model As String

    Public Sub New()
        BaseUrl = Cfg("OllamaUrl", "http://localhost:11434").TrimEnd("/"c)
        Model = Cfg("OllamaModel", "phi3:mini")
    End Sub

    Private Shared Function Cfg(key As String, fallback As String) As String
        Dim v = ConfigurationManager.AppSettings(key)
        Return If(String.IsNullOrWhiteSpace(v), fallback, v)
    End Function

    ''' Non-streaming completion. Raises on transport/HTTP errors so the caller
    ''' can show a precise message.
    Public Async Function AskAsync(systemContext As String, question As String) As Task(Of String)
        Dim payload = New With {
            .model = Model,
            .prompt = systemContext & vbCrLf & vbCrLf & "Question: " & question,
            .stream = False
        }
        Dim content = New StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        Dim response = Await http.PostAsync(BaseUrl & "/api/generate", content)
        Dim body = Await response.Content.ReadAsStringAsync()
        If Not response.IsSuccessStatusCode Then
            ' Ollama returns {"error":"model 'x' not found, try pulling it first"}
            Try
                Using doc = JsonDocument.Parse(body)
                    Dim err As JsonElement
                    If doc.RootElement.TryGetProperty("error", err) Then
                        Throw New ApplicationException(err.GetString())
                    End If
                End Using
            Catch ex As ApplicationException
                Throw
            Catch
            End Try
            Throw New ApplicationException($"AI server returned {CInt(response.StatusCode)}.")
        End If
        Using doc = JsonDocument.Parse(body)
            Return doc.RootElement.GetProperty("response").GetString()
        End Using
    End Function

    Public Class Diagnosis
        Public Property ServerReachable As Boolean
        Public Property ModelInstalled As Boolean
        Public Property InstalledModels As New List(Of String)
        Public Property Detail As String

        Public ReadOnly Property Ready As Boolean
            Get
                Return ServerReachable AndAlso ModelInstalled
            End Get
        End Property
    End Class

    ''' Ask the server what's going on so the UI can give exact guidance.
    Public Async Function DiagnoseAsync() As Task(Of Diagnosis)
        Dim d As New Diagnosis()
        Try
            Using cts As New Threading.CancellationTokenSource(TimeSpan.FromSeconds(4))
                Dim resp = Await http.GetAsync(BaseUrl & "/api/tags", cts.Token)
                d.ServerReachable = resp.IsSuccessStatusCode
                If resp.IsSuccessStatusCode Then
                    Dim body = Await resp.Content.ReadAsStringAsync()
                    Using doc = JsonDocument.Parse(body)
                        Dim models As JsonElement
                        If doc.RootElement.TryGetProperty("models", models) Then
                            For Each m In models.EnumerateArray()
                                Dim nm As JsonElement
                                If m.TryGetProperty("name", nm) Then d.InstalledModels.Add(nm.GetString())
                            Next
                        End If
                    End Using
                    d.ModelInstalled = d.InstalledModels.Any(Function(n) n = Model OrElse n.StartsWith(Model & ":") OrElse n.Split(":"c)(0) = Model.Split(":"c)(0))
                    d.Detail = If(d.ModelInstalled, "Ready.",
                        $"Server is running but the model '{Model}' isn't pulled yet.")
                Else
                    d.Detail = $"Server answered with status {CInt(resp.StatusCode)}."
                End If
            End Using
        Catch ex As TaskCanceledException
            d.Detail = "No response from " & BaseUrl & " (is Ollama running?)."
        Catch ex As Exception
            d.Detail = "Can't reach " & BaseUrl & " — " & ex.Message
        End Try
        Return d
    End Function

    Public Async Function IsAvailableAsync() As Task(Of Boolean)
        Return (Await DiagnoseAsync()).ServerReachable
    End Function

    ''' Best-effort: launch "ollama serve" if the executable is on PATH.
    ''' Returns True if a start was attempted.
    Public Shared Function TryStartServer() As Boolean
        Try
            Dim psi As New ProcessStartInfo("ollama", "serve") With {
                .UseShellExecute = False, .CreateNoWindow = True,
                .RedirectStandardOutput = True, .RedirectStandardError = True}
            Process.Start(psi)
            Return True
        Catch
            Return False
        End Try
    End Function

End Class
