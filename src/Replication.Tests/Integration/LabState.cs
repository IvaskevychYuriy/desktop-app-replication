using Microsoft.Data.Sqlite;
using Replication.Client;
using Replication.Model;
using Sample.Lab;

namespace Replication.Tests.Integration;

/// <summary>State of the lab's databases for assertions, read with raw SQL (<see cref="Rows"/>).</summary>
internal static class LabState
{
    public static long Head(this Lab lab) => lab.Owner.Head();

    public static Guid ItemId(this Lab lab, string name) =>
        Rows.All(lab.Owner.DbPath, "Item").Single(x => (string?)x.Value["Name"] == name).Key;

    public static Guid CategoryId(this Lab lab, string name) =>
        Rows.All(lab.Owner.DbPath, "Category").Single(x => (string?)x.Value["Name"] == name).Key;

    public static RowData OwnerRow(this Lab lab, string table, Guid id) => Rows.One(lab.Owner.DbPath, table, id);

    public static bool OwnerHas(this Lab lab, string table, Guid id) => Rows.Has(lab.Owner.DbPath, table, id);

    /// <summary>The row in the client's replica of the owner (<c>InstanceId</c> = the owner's instance id).</summary>
    public static RowData ReplicaRow(this ClientNode c, string table, Guid id) => Rows.One(c.DbPath, table, id, c.Instance);

    public static bool ReplicaHas(this ClientNode c, string table, Guid id) => Rows.Has(c.DbPath, table, id, c.Instance);

    public static List<OutboxEntry> Outbox(this ClientNode c) => c.Replication.Store.Entries(c.Instance);

    /// <summary>Replica equals owner: same ids, versions and data columns in every table.</summary>
    public static List<string> Diff(this Lab lab, ClientNode c) => Rows.Diff(lab.Owner.Store.Model, lab.Owner.DbPath, c.DbPath, c.Instance);

    public static long MaskOf(this Lab lab, string table, params string[] columns) => lab.Owner.Store.Model[table].MaskOf(columns);

    /// <summary>The owner's tombstone of a deleted row: version and origin, or null.</summary>
    public static (long Version, string? Origin)? Tombstone(this Lab lab, string table, Guid id)
    {
        using SqliteConnection c = lab.Owner.Store.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version, origin FROM _sync_tombstones WHERE tbl = @t AND pk = @p";
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@p", Wire.PkText(id));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1)) : null;
    }
}
