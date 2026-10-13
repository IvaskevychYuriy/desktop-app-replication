using System.Globalization;
using Microsoft.Data.Sqlite;
using Replication.Model;

namespace Replication.Tests.Integration;

/// <summary>A row read with raw SQL: data and service columns by name.</summary>
internal sealed class RowData(Dictionary<string, object?> values)
{
    public object? this[string column] => values[column];

    public long Version => (long)values[SyncColumns.Version]!;
    public long Base => (long)values[SyncColumns.Base]!;
    public long Mask => (long)values[SyncColumns.Mask]!;
    public string? Origin => (string?)values[SyncColumns.Origin];
    public string? Instance => (string?)values[SyncColumns.InstanceId];
}

/// <summary>Raw SQL reads of an owner database or a client replica for assertions; they never go through the sync code.</summary>
internal static class Rows
{
    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    /// <summary>Rows of a table by Id; on a client pass <paramref name="instance"/> to read one replica.</summary>
    public static Dictionary<Guid, RowData> All(string dbPath, string table, string? instance = null)
    {
        using SqliteConnection c = Open(dbPath);
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Wire.Q(table)}" + (instance is null ? "" : " WHERE InstanceId = @i");
        if (instance is not null)
            cmd.Parameters.AddWithValue("@i", instance);
        using SqliteDataReader r = cmd.ExecuteReader();
        var rows = new Dictionary<Guid, RowData>();
        while (r.Read())
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < r.FieldCount; i++)
                values[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows[Guid.Parse(r.GetString(r.GetOrdinal(SyncColumns.Key)))] = new RowData(values);
        }
        return rows;
    }

    public static RowData One(string dbPath, string table, Guid id, string? instance = null) =>
        All(dbPath, table, instance).TryGetValue(id, out RowData? row) ? row : throw new InvalidOperationException($"{table} {id}: no such row in {Path.GetFileName(dbPath)}");

    public static bool Has(string dbPath, string table, Guid id, string? instance = null) => All(dbPath, table, instance).ContainsKey(id);

    /// <summary>Names of the sync triggers on the table (design 5.2: insert, update, delete).</summary>
    public static List<string> Triggers(string dbPath, string table)
    {
        using SqliteConnection c = Open(dbPath);
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = @t AND name LIKE '\\_sync\\_%' ESCAPE '\\' ORDER BY name";
        cmd.Parameters.AddWithValue("@t", table);
        using SqliteDataReader r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read())
            names.Add(r.GetString(0));
        return names;
    }

    public static object? Scalar(string dbPath, string sql)
    {
        using SqliteConnection c = Open(dbPath);
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>
    /// Differences between the owner's rows and one replica on a client: ids, <c>SyncVersion</c> and every data column.
    /// Empty when the replica equals the owner (design 17, "convergence").
    /// </summary>
    public static List<string> Diff(SyncModel model, string ownerPath, string clientPath, string instance)
    {
        var diffs = new List<string>();
        foreach (SyncTable t in model.Tables)
        {
            Dictionary<Guid, RowData> owner = All(ownerPath, t.Name);
            Dictionary<Guid, RowData> replica = All(clientPath, t.Name, instance);
            foreach ((Guid id, RowData o) in owner)
            {
                if (!replica.TryGetValue(id, out RowData? r))
                    diffs.Add($"{t.Name} {id}: missing in the replica");
                else if (Text(t, o) != Text(t, r))
                    diffs.Add($"{t.Name} {id}: owner [{Text(t, o)}], replica [{Text(t, r)}]");
            }
            foreach (Guid id in replica.Keys.Where(k => !owner.ContainsKey(k)))
                diffs.Add($"{t.Name} {id}: not on the owner");
        }
        return diffs;
    }

    private static string Text(SyncTable t, RowData row) =>
        string.Join("|", new[] { SyncColumns.Version }.Concat(t.Columns).Select(c => $"{c}={Convert.ToString(row[c], CultureInfo.InvariantCulture) ?? "∅"}"));
}
