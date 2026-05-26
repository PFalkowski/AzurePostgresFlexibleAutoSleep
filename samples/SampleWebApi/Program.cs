using AzurePostgresFlexibleAutoSleep;
using AzurePostgresFlexibleAutoSleep.Activity;
using AzurePostgresFlexibleAutoSleep.DependencyInjection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAzurePostgresAutoSleep(opts =>
{
    opts.ResourceId =
        builder.Configuration["Postgres:ResourceId"]
        ?? "/subscriptions/REPLACE/resourceGroups/REPLACE/providers/Microsoft.DBforPostgreSQL/flexibleServers/REPLACE";
    opts.IdleThreshold = TimeSpan.FromMinutes(15);
    opts.ExemptPaths = new() { "/healthz", "/api/purchase/webhook" };
});

builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
{
    var connStr = builder.Configuration.GetConnectionString("Default")
        ?? "Host=localhost;Database=sample;Username=app;Password=app";
    opt.UseNpgsql(connStr)
       .AddInterceptors(sp.GetRequiredService<ActivityCommandInterceptor>());
});

builder.Services.AddHostedService<NightlyJob>();

var app = builder.Build();

app.UseAzurePostgresAutoSleep();
app.UseRouting();

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/api/things", async (AppDbContext db) => Results.Ok(await db.Things.ToListAsync()));
app.MapPost("/api/purchase/webhook", () => Results.Ok());

app.Run();

internal sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Thing> Things => Set<Thing>();
}

internal sealed class Thing
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal sealed class NightlyJob(IDbWaker waker, IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await waker.EnsureAwakeAsync(stoppingToken);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = await db.Things.ToListAsync(stoppingToken);
        }
    }
}
