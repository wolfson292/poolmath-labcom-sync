using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using PoolSync.Configuration;
using PoolSync.LabCom;
using PoolSync.PoolMath;
using PoolSync.State;
using PoolSync.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "POOLSYNC_");

builder.Services.Configure<LabComOptions>(
    builder.Configuration.GetSection(LabComOptions.SectionName));
builder.Services.Configure<PoolMathOptions>(
    builder.Configuration.GetSection(PoolMathOptions.SectionName));
builder.Services.Configure<MappingOptions>(
    builder.Configuration.GetSection(MappingOptions.SectionName));
builder.Services.Configure<List<WaterBodyOptions>>(
    builder.Configuration.GetSection("WaterBodies"));

builder.Services.AddOptions<SyncOptions>()
    .Bind(builder.Configuration.GetSection(SyncOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<SyncStatus>();
builder.Services.AddSingleton<SyncRunner>();
builder.Services.AddSingleton<ISyncStateStore, FileSyncStateStore>();
builder.Services.AddScoped<ReadingMapper>();

builder.Services.AddHttpClient<LabComClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    })
    .AddStandardResilienceHandler();

// The real client is always registered so `list-pools` works even in dry-run mode; only what
// IPoolMathClient resolves to changes.
builder.Services.AddHttpClient<PoolMathClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    })
    .AddStandardResilienceHandler();

var dryRun = builder.Configuration.GetValue($"{SyncOptions.SectionName}:DryRun", true);
if (dryRun)
{
    builder.Services.AddScoped<IPoolMathClient, DryRunPoolMathClient>();
}
else
{
    builder.Services.AddScoped<IPoolMathClient>(sp => sp.GetRequiredService<PoolMathClient>());
}

builder.Services.AddHostedService<SyncWorker>();

var app = builder.Build();

// Discovery helpers: both ids in the config have to be looked up once, and neither service exposes
// them anywhere convenient.
if (args.Contains("list-accounts") || args.Contains("list-pools") || args.Contains("print-token"))
{
    await RunDiscoveryAsync(app, args);
    return;
}

// wwwroot/index.html is the root page: status for each water body plus a manual sync button.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", (SyncStatus status) =>
    status.IsHealthy
        ? Results.Ok(new { status = "healthy", status.LastSuccessAt })
        : Results.Json(
            new { status = "unhealthy", status.LastError, status.ConsecutiveFailures },
            statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/status", (SyncStatus status, IOptions<SyncOptions> sync) => Results.Ok(new
{
    status.LastRunStartedAt,
    status.LastSuccessAt,
    status.LastError,
    status.LastErrorAt,
    status.ConsecutiveFailures,
    dryRun = sync.Value.DryRun,
    interval = sync.Value.Interval.ToString(),
    waterBodies = status.WaterBodies.Values.OrderBy(w => w.Name),
}));

// Manual trigger for the button on the root page. A run already in progress is reported as such
// rather than queued, so the page can say so instead of appearing to hang.
app.MapPost("/sync", async (SyncRunner runner, CancellationToken ct) =>
{
    var result = await runner.RunAsync(ct);

    return result.Outcome switch
    {
        "busy" => Results.Json(result, statusCode: StatusCodes.Status409Conflict),
        "failed" => Results.Json(result, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.Ok(result),
    };
});

await app.RunAsync();
return;

static async Task RunDiscoveryAsync(WebApplication app, string[] args)
{
    using var scope = app.Services.CreateScope();

    if (args.Contains("list-accounts"))
    {
        var labCom = scope.ServiceProvider.GetRequiredService<LabComClient>();
        var cloudAccount = await labCom.GetCloudAccountAsync(CancellationToken.None);

        Console.WriteLine($"LabCOM cloud account {cloudAccount.Id} ({cloudAccount.Email})");
        foreach (var account in cloudAccount.Accounts)
        {
            Console.WriteLine(
                $"  LabComAccountId: {account.Id,-10} {account.DisplayName} " +
                $"({account.Measurements.Count} measurements)");
        }
    }

    if (args.Contains("print-token"))
    {
        var poolMath = scope.ServiceProvider.GetRequiredService<PoolMathClient>();
        var credentials = await poolMath.AuthenticateAsync(CancellationToken.None);

        Console.WriteLine("Pool Math credentials (store these instead of the password):");
        Console.WriteLine($"  POOLSYNC_PoolMath__UserId={credentials.UserId}");
        Console.WriteLine($"  POOLSYNC_PoolMath__AuthToken={credentials.AuthToken}");
    }

    if (args.Contains("list-pools"))
    {
        var poolMath = scope.ServiceProvider.GetRequiredService<PoolMathClient>();
        var pools = await poolMath.ListPoolsAsync(CancellationToken.None);

        Console.WriteLine("Pool Math pools:");
        foreach (var pool in pools)
        {
            Console.WriteLine($"  PoolMathPoolId: {pool.Id}  {pool.Name}");
        }
    }
}
