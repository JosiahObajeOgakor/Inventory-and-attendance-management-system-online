Imports System.IO
Imports System.Reflection

''' Where the app's own files live. Resolved from the assembly rather than the
''' process, so the same code finds Assets\ and Schema.sql when the app runs
''' normally and when the test runner loads it.
Public Module AppPaths

    Public ReadOnly Property BaseDir As String
        Get
            Return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        End Get
    End Property

    Public Function Asset(fileName As String) As String
        Return Path.Combine(BaseDir, "Assets", fileName)
    End Function

    Public ReadOnly Property SchemaFile As String
        Get
            Return Path.Combine(BaseDir, "Schema.sql")
        End Get
    End Property

End Module
