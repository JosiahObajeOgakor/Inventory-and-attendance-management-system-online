Imports System.Data
Imports System.Data.SqlClient
Imports System.Configuration

''' Thin ADO.NET helper shared by every form. Always use parameters — never
''' concatenate user input into SQL text. Connection string comes from
''' App.config (StockDeskDB) — edit that file to point at your SQL Server.
Public Class DataAccess

    Private Shared ReadOnly ConnString As String =
        ConfigurationManager.ConnectionStrings("StockDeskDB").ConnectionString

    Private Shared Function NewConnection() As SqlConnection
        Return New SqlConnection(ConnString)
    End Function

    ''' Runs a SELECT and returns a DataTable.
    Public Shared Function GetTable(sql As String, Optional params As Dictionary(Of String, Object) = Nothing) As DataTable
        Using conn = NewConnection()
            Using cmd As New SqlCommand(sql, conn)
                AddParams(cmd, params)
                conn.Open()
                Dim table As New DataTable()
                table.Load(cmd.ExecuteReader())
                Return table
            End Using
        End Using
    End Function

    ''' Runs an INSERT/UPDATE/DELETE. Returns rows affected.
    Public Shared Function Execute(sql As String, Optional params As Dictionary(Of String, Object) = Nothing) As Integer
        Try
            Using conn = NewConnection()
                Using cmd As New SqlCommand(sql, conn)
                    AddParams(cmd, params)
                    conn.Open()
                    Return cmd.ExecuteNonQuery()
                End Using
            End Using
        Catch ex As SqlException When DbMaintenance.IsFullError(ex)
            DbMaintenance.NotifyFull()
            Throw
        End Try
    End Function

    ''' Runs an INSERT and returns the new identity value — used for Invoices/PurchaseOrders headers.
    Public Shared Function ExecuteScalarInsert(sql As String, params As Dictionary(Of String, Object)) As Integer
        Try
            Using conn = NewConnection()
                Using cmd As New SqlCommand(sql & "; SELECT CAST(SCOPE_IDENTITY() AS INT);", conn)
                    AddParams(cmd, params)
                    conn.Open()
                    Return CInt(cmd.ExecuteScalar())
                End Using
            End Using
        Catch ex As SqlException When DbMaintenance.IsFullError(ex)
            DbMaintenance.NotifyFull()
            Throw
        End Try
    End Function

    ''' Runs several statements as one atomic transaction — use for any save that
    ''' touches more than one table (new invoice: header + lines + stock + ledger;
    ''' new PO: header + lines). Each entry is (sql, params); later statements can't
    ''' see earlier ones' SCOPE_IDENTITY() with this simple form — for that, write a
    ''' dedicated method (see ExecuteScalarInsert pattern) inside a manual
    ''' SqlTransaction instead of using this list form.
    Public Shared Sub ExecuteTransaction(statements As List(Of (Sql As String, Params As Dictionary(Of String, Object))))
        Using conn = NewConnection()
            conn.Open()
            Dim tx = conn.BeginTransaction()
            Try
                For Each stmt In statements
                    Using cmd As New SqlCommand(stmt.Sql, conn, tx)
                        AddParams(cmd, stmt.Params)
                        cmd.ExecuteNonQuery()
                    End Using
                Next
                tx.Commit()
            Catch ex As Exception
                tx.Rollback()
                If TypeOf ex Is SqlException AndAlso DbMaintenance.IsFullError(DirectCast(ex, SqlException)) Then DbMaintenance.NotifyFull()
                Throw
            End Try
        End Using
    End Sub

    ''' Runs several related writes as ONE transaction, with the connection handed
    ''' to the caller so later statements can use earlier SCOPE_IDENTITY values
    ''' (a sale: header → lines → stock → ledger → payment → rebate). Anything
    ''' thrown rolls the whole lot back, so a crash mid-save can never leave a
    ''' half-written sale behind.
    Public Shared Function InTransaction(Of T)(work As Func(Of SqlConnection, SqlTransaction, T)) As T
        Try
            Using conn = NewConnection()
                conn.Open()
                Dim tx = conn.BeginTransaction()
                Try
                    Dim result = work(conn, tx)
                    tx.Commit()
                    Return result
                Catch
                    Try
                        tx.Rollback()
                    Catch
                    End Try
                    Throw
                End Try
            End Using
        Catch ex As SqlException When DbMaintenance.IsFullError(ex)
            DbMaintenance.NotifyFull()
            Throw
        End Try
    End Function

    ''' One statement inside an open transaction. Returns rows affected.
    Public Shared Function Exec(conn As SqlConnection, tx As SqlTransaction, sql As String,
                                Optional params As Dictionary(Of String, Object) = Nothing) As Integer
        Using cmd As New SqlCommand(sql, conn, tx)
            AddParams(cmd, params)
            Return cmd.ExecuteNonQuery()
        End Using
    End Function

    ''' An INSERT inside an open transaction; returns the new identity value.
    Public Shared Function InsertReturningId(conn As SqlConnection, tx As SqlTransaction, sql As String,
                                             params As Dictionary(Of String, Object)) As Integer
        Using cmd As New SqlCommand(sql & "; SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx)
            AddParams(cmd, params)
            Return CInt(cmd.ExecuteScalar())
        End Using
    End Function

    ''' A single value inside an open transaction.
    Public Shared Function ScalarIn(conn As SqlConnection, tx As SqlTransaction, sql As String,
                                    Optional params As Dictionary(Of String, Object) = Nothing) As Object
        Using cmd As New SqlCommand(sql, conn, tx)
            AddParams(cmd, params)
            Return cmd.ExecuteScalar()
        End Using
    End Function

    ''' A SELECT inside an open transaction, for when a later write in the same
    ''' transaction depends on rows read earlier in it (so the read sees the
    ''' transaction's own uncommitted state and can't race a concurrent writer).
    Public Shared Function TableIn(conn As SqlConnection, tx As SqlTransaction, sql As String,
                                   Optional params As Dictionary(Of String, Object) = Nothing) As DataTable
        Using cmd As New SqlCommand(sql, conn, tx)
            AddParams(cmd, params)
            Dim table As New DataTable()
            table.Load(cmd.ExecuteReader())
            Return table
        End Using
    End Function

    ''' Tries to open the configured connection and reports success/failure in
    ''' plain English — wired to the "Test database connection" button on login.
    Public Shared Function TestConnection() As String
        Try
            Using conn = NewConnection()
                conn.Open()
                Dim b As New SqlConnectionStringBuilder(ConnString)
                Return "Connected successfully." & vbCrLf & "Server: " & b.DataSource & vbCrLf & "Database: " & b.InitialCatalog
            End Using
        Catch ex As Exception
            Return "Connection failed:" & vbCrLf & ex.Message
        End Try
    End Function

    Private Shared Sub AddParams(cmd As SqlCommand, params As Dictionary(Of String, Object))
        If params Is Nothing Then Return
        For Each kv In params
            cmd.Parameters.AddWithValue(kv.Key, If(kv.Value, DBNull.Value))
        Next
    End Sub

End Class
