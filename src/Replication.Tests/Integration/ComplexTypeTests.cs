using NUnit.Framework;
using Replication.Model;
using Sample.Lab;

namespace Replication.Tests.Integration;

/// <summary>
/// A complex type (<c>[ComplexType] ObjectSettings</c>) is stored in columns of its owner's table, <c>Settings_Description</c>
/// and <c>Settings_StockLevel</c>. They are data columns like any other: in the mask, the triggers and the wire.
/// </summary>
[TestFixture]
public sealed class ComplexTypeTests : LabTests
{
    [Test]
    public async Task Sync_model_has_a_column_for_each_property_of_the_complex_type()
    {
        Lab lab = await StartAsync();

        Assert.That(lab.Owner.Store.Model["StockObject"].Columns, Is.SupersetOf(new[] { "Settings_Description", "Settings_StockLevel" }));
    }

    [Test]
    public async Task Insert_on_the_owner_is_inserted_into_the_client_with_the_complex_type()
    {
        Lab lab = await StartSyncedAsync();

        Guid id = await lab.Owner.NewStockObjectAsync("Палета", "Верхній ярус", 7);
        await lab.SettleAsync();

        RowData row = lab.C1.ReplicaRow("StockObject", id);
        Assert.That(row["Settings_Description"], Is.EqualTo("Верхній ярус"));
        Assert.That(row["Settings_StockLevel"], Is.EqualTo(7L));
        Assert.That(row.Version, Is.EqualTo(lab.OwnerRow("StockObject", id).Version));
        Assert.That(lab.Diff(lab.C1), Is.Empty);
    }

    [Test]
    public async Task Snapshot_carries_the_complex_type()
    {
        Lab lab = await StartAsync();
        Guid id = await lab.Owner.NewStockObjectAsync("Стелаж", null, 3);

        await lab.SyncNowAsync(lab.C1);
        await lab.SettleAsync();

        RowData row = lab.C1.ReplicaRow("StockObject", id);
        Assert.That(row["Settings_Description"], Is.Null);
        Assert.That(row["Settings_StockLevel"], Is.EqualTo(3L));
        Assert.That(lab.Diff(lab.C1), Is.Empty);
    }
}
