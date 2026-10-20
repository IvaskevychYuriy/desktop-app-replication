using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Sample.Domain;

public static class SampleDbHostingExtensions
{
    public static IServiceCollection AddSampleDb(this IServiceCollection services, string dbPath) =>
        services.AddDbContextFactory<SampleDbContext>(o => SampleDbContext.Configure(o, dbPath));

    /// <summary>Brings the owner database up to the current model. Call before <c>InstallOwnerStore</c>.</summary>
    public static IHost MigrateSampleDb(this IHost host)
    {
        using SampleDbContext db = host.Services.GetRequiredService<IDbContextFactory<SampleDbContext>>().CreateDbContext();
        // The sample ships no migrations yet: create the schema straight from the model.
        if (db.Database.GetMigrations().Any())
            db.Database.Migrate();
        else
            db.Database.EnsureCreated();
        return host;
    }
}
