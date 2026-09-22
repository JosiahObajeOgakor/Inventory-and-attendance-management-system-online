using Microsoft.Data.SqlClient;

namespace Inventory.MigrationTool;

public sealed class Row(Dictionary<string, object?> v)
{
    private object? Raw(string c) => v.TryGetValue(c, out var x) && x is not DBNull ? x : null;
    public bool Has(string c) => v.ContainsKey(c);
    public int Int(string c) => Convert.ToInt32(Raw(c));
    public int? IntN(string c) => Raw(c) is { } x ? Convert.ToInt32(x) : null;
    public decimal Dec(string c) => Convert.ToDecimal(Raw(c));
    public bool Bool(string c) => Raw(c) is { } x && Convert.ToBoolean(x);
    public string Str(string c) => Convert.ToString(Raw(c)) ?? "";
    public string? StrN(string c) => Raw(c) is { } x ? Convert.ToString(x) : null;
    public DateOnly Date(string c) => DateOnly.FromDateTime(Convert.ToDateTime(Raw(c)));
    public DateOnly? DateN(string c) => Raw(c) is { } x ? DateOnly.FromDateTime(Convert.ToDateTime(x)) : null;
    public DateTime DateTimeRaw(string c) => Convert.ToDateTime(Raw(c));
    public DateTime? DateTimeRawN(string c) => Raw(c) is { } x ? Convert.ToDateTime(x) : null;
}

/// <summary>
/// READ-ONLY access to the SQL Server source. Run it with a login that only has db_datareader: even a bug in this tool
/// then cannot modify the original database. Every statement issued here is a SELECT.
/// </summary>
public sealed class SourceReader(string connectionString) : IDisposable
{
    private readonly SqlConnection _conn = Open(connectionString);

    private static SqlConnection Open(string cs)
    {
        var c = new SqlConnection(cs);
        c.Open();
        return c;
    }

    public List<Row> Query(string sql)
    {
        if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The source is read-only: only SELECT statements are allowed.");
        using var cmd = new SqlCommand(sql, _conn) { CommandTimeout = 120 };
        using var r = cmd.ExecuteReader();
        var rows = new List<Row>();
        while (r.Read())
        {
            var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = r.GetValue(i);
            rows.Add(new Row(d));
        }
        return rows;
    }

    public List<Row> Table(string name) => Query($"SELECT * FROM [{name}]");

    public bool TableExists(string name) =>
        Query($"SELECT 1 AS x FROM sys.tables WHERE name = '{name.Replace("'", "''")}'").Count > 0;

    public decimal Scalar(string sql) => Query(sql) is { Count: > 0 } r ? r[0].Dec("v") : 0m;

    public void Dispose() => _conn.Dispose();
}
