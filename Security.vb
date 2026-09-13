Imports System.Security.Cryptography
Imports System.Text

''' Password hashing + login policy. Hashes are PBKDF2-SHA256, stored as
'''   pbkdf2$<iterations>$<saltBase64>$<hashBase64>
''' Never store or log a plain-text password.
Public Module Security

    Private Const Iterations As Integer = 120000
    Private Const SaltBytes As Integer = 16
    Private Const HashBytes As Integer = 32

    Public Const MaxFailedAttempts As Integer = 5
    Public ReadOnly LockoutWindow As TimeSpan = TimeSpan.FromMinutes(15)

    ''' Produce a storable hash string for a new / changed password.
    Public Function HashPassword(password As String) As String
        Dim salt(SaltBytes - 1) As Byte
        Using rng = RandomNumberGenerator.Create()
            rng.GetBytes(salt)
        End Using
        Dim hash = Pbkdf2(password, salt, Iterations, HashBytes)
        Return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}"
    End Function

    ''' True if `password` matches a stored pbkdf2$… hash. Placeholder/legacy
    ''' values ("SETUP_REQUIRED", "CHANGE_ME_…") never verify.
    Public Function VerifyPassword(password As String, stored As String) As Boolean
        If String.IsNullOrEmpty(stored) OrElse Not stored.StartsWith("pbkdf2$") Then Return False
        Dim parts = stored.Split("$"c)
        If parts.Length <> 4 Then Return False
        Dim iters As Integer
        If Not Integer.TryParse(parts(1), iters) Then Return False
        Dim salt = Convert.FromBase64String(parts(2))
        Dim expected = Convert.FromBase64String(parts(3))
        Dim actual = Pbkdf2(password, salt, iters, expected.Length)
        Return FixedTimeEquals(expected, actual)
    End Function

    ''' Is this a real, verifiable hash (vs a "must set password" placeholder)?
    Public Function IsRealHash(stored As String) As Boolean
        Return Not String.IsNullOrEmpty(stored) AndAlso stored.StartsWith("pbkdf2$")
    End Function

    ''' Returns Nothing if OK, otherwise a human message explaining what's weak.
    Public Function PasswordProblem(password As String) As String
        If password Is Nothing OrElse password.Length < 8 Then Return "Use at least 8 characters."
        Dim hasLetter = password.Any(AddressOf Char.IsLetter)
        Dim hasDigit = password.Any(AddressOf Char.IsDigit)
        If Not (hasLetter AndAlso hasDigit) Then Return "Mix letters and numbers."
        If password.Trim().Length <> password.Length Then Return "Remove leading/trailing spaces."
        Dim weak = {"password", "12345678", "qwerty", "admin123", "chewypets"}
        If weak.Any(Function(w) password.ToLowerInvariant().Contains(w)) Then Return "That password is too easy to guess."
        Return Nothing
    End Function

    Private Function Pbkdf2(password As String, salt As Byte(), iters As Integer, length As Integer) As Byte()
        Using kdf As New Rfc2898DeriveBytes(password, salt, iters, HashAlgorithmName.SHA256)
            Return kdf.GetBytes(length)
        End Using
    End Function

    Private Function FixedTimeEquals(a As Byte(), b As Byte()) As Boolean
        If a Is Nothing OrElse b Is Nothing OrElse a.Length <> b.Length Then Return False
        Dim diff As Integer = 0
        For i = 0 To a.Length - 1
            diff = diff Or (a(i) Xor b(i))
        Next
        Return diff = 0
    End Function

End Module
