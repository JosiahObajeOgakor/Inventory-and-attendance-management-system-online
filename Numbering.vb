Imports System.Data.SqlClient

''' Document numbers: "ChewyStock-12092026-143205" — branded, with the date and
''' time the document was actually created (not the possibly-backdated sale or
''' order date). Two entries made in the same second would produce the same
''' number; the caller retries a second or more later on a clash instead.
Public Module Numbering

    Public Function NextNumber(conn As SqlConnection, tx As SqlTransaction, prefix As String,
                         table As String, column As String, [on] As Date) As String
        Dim now = DateTime.Now
        Return $"ChewyStock-{now:ddMMyyyy}-{now:HHmmss}"
    End Function

    ''' True for "that number is already taken" — worth retrying with the next one.
    Public Function IsDuplicate(ex As SqlException) As Boolean
        For Each er As SqlError In ex.Errors
            If er.Number = 2627 OrElse er.Number = 2601 Then Return True
        Next
        Return False
    End Function

End Module
