using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sample.Domain;

namespace Sample.Owner;

/// <summary>Fills the owner with data once at start: a category with a bulk of items, then a stream of single-row writes.</summary>
public sealed class DataGenerator(IDbContextFactory<SampleDbContext> factory, ILogger<DataGenerator> log) : BackgroundService
{
    private static readonly string[] s_names = ["Ліхтарик", "Батарейки", "Клей", "Блокнот", "Лампа", "Кабель", "Фарба", "Рукавиці"];
    private static readonly string[] s_statuses = ["новий", "активний", "архів"];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            Guid category = await CreateCategoryAsync(ct);
            await InsertItemsAsync(category, 10_000, ct);
            await ChurnItemsAsync(category, 1_000, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            log.LogError(e, "Data generation failed");
        }
    }

    /// <summary>One new category with a random name.</summary>
    public async Task<Guid> CreateCategoryAsync(CancellationToken ct = default)
    {
        await using SampleDbContext db = await factory.CreateDbContextAsync(ct);
        var category = new Category { Name = $"Категорія {Guid.NewGuid():N}"[..16] };
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Created category {Name}", category.Name);
        return category.Id;
    }

    /// <summary><paramref name="count"/> new items in the category, saved in batches of 1000.</summary>
    public async Task InsertItemsAsync(Guid categoryId, int count, CancellationToken ct = default)
    {
        const int BatchSize = 1_000;
        await using SampleDbContext db = await factory.CreateDbContextAsync(ct);
        for (int i = 1; i <= count; i++)
        {
            db.Items.Add(NewItem(categoryId));
            if (i % BatchSize == 0 || i == count)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
        log.LogInformation("Inserted {Count} items in batches of {Batch}", count, BatchSize);
    }

    /// <summary>
    /// <paramref name="count"/> inserts, then <paramref name="count"/> updates of random items, then <paramref name="count"/> deletes
    /// of random items of the category. One SaveChanges per row.
    /// </summary>
    public async Task ChurnItemsAsync(Guid categoryId, int count, CancellationToken ct = default)
    {
        await using SampleDbContext db = await factory.CreateDbContextAsync(ct);
        List<Guid> ids = await db.Items.Where(x => x.CategoryId == categoryId).Select(x => x.Id).ToListAsync(ct);

        for (int i = 0; i < count; i++)
        {
            Item item = NewItem(categoryId);
            db.Items.Add(item);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            ids.Add(item.Id);
        }

        for (int i = 0; i < count && ids.Count > 0; i++)
        {
            Guid id = ids[Random.Shared.Next(ids.Count)];
            Item item = await db.Items.FirstAsync(x => x.Id == id, ct);
            item.Price = RandomPrice();
            item.Status = Pick(s_statuses);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        for (int i = 0; i < count && ids.Count > 0; i++)
        {
            int at = Random.Shared.Next(ids.Count);
            Guid id = ids[at];
            ids[at] = ids[^1];
            ids.RemoveAt(ids.Count - 1);
            db.Items.Remove(await db.Items.FirstAsync(x => x.Id == id, ct));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        log.LogInformation("Inserted, updated, deleted {Count} random items one by one", count);
    }

    private static Item NewItem(Guid categoryId) => new()
    {
        Name = $"{Pick(s_names)} {Random.Shared.Next(1_000_000)}",
        Price = RandomPrice(),
        Status = Pick(s_statuses),
        CategoryId = categoryId,
    };

    private static long RandomPrice() => 10 + Random.Shared.Next(99) * 10;

    private static T Pick<T>(T[] list) => list[Random.Shared.Next(list.Length)];
}
