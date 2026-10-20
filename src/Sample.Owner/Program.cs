using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Replication;
using Replication.Owner;
using Sample.Domain;
using Sample.Owner;

// Settings come from appsettings.json, the environment (Owner__ConnectionString, Kestrel__Endpoints__Grpc__Url) or
// the command line (--Owner:ConnectionString="Data Source=x.db").
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string connectionString = builder.Configuration["Owner:ConnectionString"] ?? "Data Source=owner.db";

var log = new SyncLog();
log.Written += e => Console.WriteLine($"{e.At:HH:mm:ss.fff} [{e.Source}] {e.Text}");
builder.Services.AddSingleton(log);
builder.Services.AddSampleOwner(connectionString);
builder.Services.AddDbContextFactory<SampleDbContext>(o => o.UseSqlite(connectionString).AddInterceptors(new StampInterceptor()));
builder.Services.AddHostedService<DataGenerator>();

WebApplication app = builder.Build();
app.MapReplicationOwner();
await app.Services.InitSampleOwnerAsync(connectionString);
await app.RunAsync();
