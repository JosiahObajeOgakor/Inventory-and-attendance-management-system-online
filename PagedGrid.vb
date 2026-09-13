Imports System.Data
Imports System.Windows.Forms

''' A grid that shows a query one page at a time (◀ Prev · Page x of y · Next ▶),
''' so long lists stay fast and readable. AllRows() returns the whole result for
''' exports — exports never depend on what page is on screen.
Public Class PagedGrid
    Inherits Panel

    Public ReadOnly Property Grid As DataGridView = UiHelpers.NewGrid()
    Private ReadOnly btnPrev As New Button() With {.Text = "◀ Prev", .AutoSize = True}
    Private ReadOnly btnNext As New Button() With {.Text = "Next ▶", .AutoSize = True}
    Private ReadOnly lblPage As New Label() With {.AutoSize = True, .Tag = "keepfont", .Margin = New Padding(10, 9, 10, 0)}
    Private ReadOnly cboSize As New ComboBox() With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 70}

    Private _sql As String
    Private _orderBy As String
    Private _params As Dictionary(Of String, Object)
    Private _source As DataTable      ' set instead of _sql when paging a computed table
    Private _page As Integer
    Private _pageSize As Integer = 50
    Private _hidden As String()
    Private _searchColumns As String()
    Private _searchTerm As String = ""

    ''' Rows per page. Screens that only have room for a handful (the Dashboard)
    ''' set this; it also becomes a choice in the pager's own dropdown.
    Public Property PageSize As Integer
        Get
            Return _pageSize
        End Get
        Set(value As Integer)
            _pageSize = Math.Max(1, value)
            If Not cboSize.Items.Contains(_pageSize) Then cboSize.Items.Insert(0, _pageSize)
            cboSize.SelectedItem = _pageSize    ' fires the handler, which repages
        End Set
    End Property

    Public Sub New()
        Dock = DockStyle.Fill
        Dim bar As New FlowLayoutPanel() With {.Dock = DockStyle.Bottom, .AutoSize = True, .Padding = New Padding(6, 4, 6, 4)}
        bar.Controls.Add(btnPrev)
        bar.Controls.Add(lblPage)
        bar.Controls.Add(btnNext)
        bar.Controls.Add(New Label() With {.Text = "Rows per page:", .AutoSize = True, .Margin = New Padding(18, 9, 4, 0)})
        cboSize.Items.AddRange(New Object() {25, 50, 100, 250})
        cboSize.SelectedItem = _pageSize
        bar.Controls.Add(cboSize)

        Controls.Add(Grid)   ' Fill first so the pager keeps its strip
        Controls.Add(bar)

        AddHandler btnPrev.Click, Sub(s, e) GoTo_(_page - 1)
        AddHandler btnNext.Click, Sub(s, e) GoTo_(_page + 1)
        AddHandler cboSize.SelectedIndexChanged, Sub(s, e)
                                                     _pageSize = CInt(cboSize.SelectedItem)
                                                     GoTo_(0)
                                                 End Sub
    End Sub

    ''' `sql` is a plain SELECT without ORDER BY; `orderBy` is required for paging.
    ''' `searchColumns`, if given, are the columns a later Search() call filters on.
    Public Sub Bind(sql As String, orderBy As String, Optional params As Dictionary(Of String, Object) = Nothing,
                     Optional hiddenColumns As String() = Nothing, Optional searchColumns As String() = Nothing)
        _sql = sql
        _source = Nothing
        _orderBy = orderBy
        _params = If(params, New Dictionary(Of String, Object))
        _hidden = If(hiddenColumns, New String() {})
        _searchColumns = If(searchColumns, New String() {})
        GoTo_(0)
    End Sub

    ''' Pages a table that's already in memory — a computed one (the Dashboard's
    ''' reorder forecast, say) that no single query can produce.
    Public Sub Bind(table As DataTable, Optional hiddenColumns As String() = Nothing, Optional searchColumns As String() = Nothing)
        _source = table
        _sql = Nothing
        _params = New Dictionary(Of String, Object)
        _hidden = If(hiddenColumns, New String() {})
        _searchColumns = If(searchColumns, New String() {})
        GoTo_(0)
    End Sub

    ''' Filters the bound query to rows where any searchColumns (from Bind) LIKE the term.
    Public Sub Search(term As String)
        _searchTerm = If(term, "").Trim()
        GoTo_(0)
    End Sub

    Public Sub Reload()
        GoTo_(_page)
    End Sub

    ''' The base query, narrowed to the active search term (if any).
    Private Function FilteredSql(ByRef p As Dictionary(Of String, Object)) As String
        p = New Dictionary(Of String, Object)(_params)
        If Not Searching() Then Return _sql
        p("@__s") = "%" & _searchTerm & "%"
        Dim clause = String.Join(" OR ", _searchColumns.Select(Function(c) $"__src.[{c}] LIKE @__s"))
        Return $"SELECT __src.* FROM ({_sql}) __src WHERE {clause}"
    End Function

    Private Function Searching() As Boolean
        Return _searchTerm <> "" AndAlso _searchColumns IsNot Nothing AndAlso _searchColumns.Length > 0
    End Function

    ''' Ordering for the query as it will actually be run. Searching wraps the
    ''' caller's SQL in a subquery, and inside that wrapper the original table
    ''' aliases are gone — "ORDER BY i.InvoiceID" stops resolving. Only the
    ''' selected column names survive, so drop the alias and order by those.
    Private Function OrderByClause() As String
        If Not Searching() Then Return _orderBy
        Return String.Join(", ", _orderBy.Split(","c).Select(
            Function(term)
                Dim parts = term.Trim().Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
                Dim column = parts(0)
                Dim dot = column.LastIndexOf("."c)
                If dot >= 0 Then column = column.Substring(dot + 1)
                column = "__src.[" & column.Trim("["c, "]"c) & "]"
                Return String.Join(" ", {column}.Concat(parts.Skip(1)))
            End Function))
    End Function

    ''' The in-memory rows the active search leaves behind.
    Private Function FilteredRows() As List(Of DataRow)
        Dim rows = _source.AsEnumerable().ToList()
        If _searchTerm = "" OrElse _searchColumns Is Nothing OrElse _searchColumns.Length = 0 Then Return rows
        Dim columns = _searchColumns.Where(Function(c) _source.Columns.Contains(c)).ToArray()
        Return rows.Where(Function(r) columns.Any(
            Function(c) Convert.ToString(r(c)).IndexOf(_searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)).ToList()
    End Function

    Private Sub GoTo_(page As Integer)
        Dim total As Integer
        Dim pageTable As DataTable

        If _source IsNot Nothing Then
            Dim rows = FilteredRows()
            total = rows.Count
            _page = ClampPage(page, total)
            pageTable = _source.Clone()
            For Each r In rows.Skip(_page * _pageSize).Take(_pageSize)
                pageTable.ImportRow(r)
            Next
        ElseIf _sql IsNot Nothing Then
            Dim p As Dictionary(Of String, Object) = Nothing
            Dim sql = FilteredSql(p)
            total = Convert.ToInt32(DataAccess.GetTable($"SELECT COUNT(*) FROM ({sql}) q", p).Rows(0)(0))
            _page = ClampPage(page, total)
            p("@__skip") = _page * _pageSize
            p("@__take") = _pageSize
            pageTable = DataAccess.GetTable($"{sql} ORDER BY {OrderByClause()} OFFSET @__skip ROWS FETCH NEXT @__take ROWS ONLY", p)
        Else
            Return
        End If

        Grid.DataSource = pageTable
        For Each c In _hidden
            If Grid.Columns.Contains(c) Then Grid.Columns(c).Visible = False
        Next

        Dim pages = PageCount(total)
        lblPage.Text = If(total = 0, "No records", $"Page {_page + 1} of {pages}   ·   {total:#,0} records")
        btnPrev.Enabled = _page > 0
        btnNext.Enabled = _page < pages - 1
        RaiseEvent PageBound(Me, EventArgs.Empty)
    End Sub

    ''' Raised after every page is bound. Row-level styling has to be re-applied
    ''' here rather than once after Bind: turning a page rebuilds the rows, and
    ''' any colouring done outside this event would be lost with them.
    Public Event PageBound As EventHandler

    Private Function PageCount(total As Integer) As Integer
        Return Math.Max(1, CInt(Math.Ceiling(total / CDbl(_pageSize))))
    End Function

    Private Function ClampPage(page As Integer, total As Integer) As Integer
        Return Math.Max(0, Math.Min(page, PageCount(total) - 1))
    End Function

    ''' Every row (not just the visible page), for Excel/CSV export.
    Public Function AllRows() As DataTable
        Dim t As DataTable
        If _source IsNot Nothing Then
            t = _source.Clone()
            For Each r In FilteredRows()
                t.ImportRow(r)
            Next
        Else
            Dim p As Dictionary(Of String, Object) = Nothing
            Dim sql = FilteredSql(p)
            t = DataAccess.GetTable($"{sql} ORDER BY {OrderByClause()}", p)
        End If
        For Each c In _hidden
            If t.Columns.Contains(c) Then t.Columns.Remove(c)
        Next
        Return t
    End Function

End Class
