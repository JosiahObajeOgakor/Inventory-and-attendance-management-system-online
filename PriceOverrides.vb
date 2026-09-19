Imports System.Data
Imports System.Data.SqlClient

''' Audit trail for manual price overrides on a sale or quotation line — who
''' changed a product's price away from its standard tier price, on which
''' document, and to what. Written inside the same transaction as the sale or
''' quotation it belongs to (see Sales.SaveOnce / Quotations.SaveOnce), so it
''' can never exist without, or be missing from, the document it documents.
Public Module PriceOverrides

    ''' Logs one overridden line. Call only when `overridePrice` actually
    ''' differs from `standardPrice` — callers decide that, so a line priced
    ''' exactly at its tier price never adds noise here.
    Public Sub Log(conn As SqlConnection, tx As SqlTransaction, docType As String, docNumber As String,
                   productName As String, standardPrice As Decimal, overridePrice As Decimal, changedByName As String)
        DataAccess.Exec(conn, tx,
            "INSERT INTO PriceOverrides (DocType, DocNumber, ProductName, StandardPrice, OverridePrice, ChangedByName) " &
            "VALUES (@t, @n, @p, @std, @ovr, @who)",
            New Dictionary(Of String, Object) From {
                {"@t", docType}, {"@n", docNumber}, {"@p", productName},
                {"@std", standardPrice}, {"@ovr", overridePrice}, {"@who", changedByName}})
    End Sub

    ''' Every override on record, most recent first — the whole point of the
    ''' trail is being able to answer "who changed what" without hunting.
    Public Function History() As DataTable
        Return DataAccess.GetTable(
            "SELECT DocType, DocNumber, ProductName, StandardPrice, OverridePrice, ChangedByName, ChangedAt " &
            "FROM PriceOverrides ORDER BY ChangedAt DESC")
    End Function

End Module
