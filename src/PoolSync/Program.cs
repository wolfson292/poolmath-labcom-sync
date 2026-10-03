using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using PoolSync.Configuration;
using PoolSync.Import;
using PoolSync.LabCom;
using PoolSync.PoolMath;
using PoolSync.State;
using PoolSync.Storage;
using PoolSync.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "POOLSYNC_");

builder.Services.Configure<LabComOptions>(
    builder.Configuration.GetSection(LabComOptions.SectionName));
builder.Services.Configure<PoolMathOptions>(
    builder.Configuration.GetSection(PoolMathOptions.SectionName));
builder.Services.Configure<MappingOptions>(
    builder.Configuration.GetSection(MappingOptions.SectionName));
builder.Services.Configure<BalanceOptions>(
    builder.Configuration.GetSection(BalanceOptions.SectionName));
builder.Services.Configure<List<WaterBodyOptions>>(
    builder.Configuration.GetSection("WaterBodies"));

builder.Services.AddOptions<SyncOptions>()
    .Bind(builder.Configuration.GetSection(SyncOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Surface and other enums go over the wire by name, which is what the settings form sends back.
builder.Services.ConfigureHttpJsonOptions(
    o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddSingleton<PoolDatabase>();
builder.Services.AddScoped<PoolMathImporter>();
builder.Services.AddSingleton<SyncStatus>();
builder.Services.AddSingleton<PoolMathCredentialCache>();
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

app.MapGet("/status", async (
    SyncStatus status, IOptions<SyncOptions> sync, IOptions<PoolMathOptions> poolMath, PoolDatabase database,
    CancellationToken ct) => Results.Ok(new
{
    status.LastRunStartedAt,
    status.LastSuccessAt,
    status.LastError,
    status.LastErrorAt,
    status.ConsecutiveFailures,
    dryRun = sync.Value.DryRun,
    interval = sync.Value.Interval.ToString(),
    poolMath = new { poolMath.Value.Enabled, poolMath.Value.WriteLogs },
    stored = await database.CountsAsync(ct),
    waterBodies = status.WaterBodies.Values.OrderBy(w => w.Name),
}));

// Manual trigger for the button on the root page. A run already in progress is reported as such
// rather than queued, so the page can say so instead of appearing to hang. It skips the settle
// time, so a test finished a moment ago is written now rather than on a later tick.
app.MapPost("/sync", async (SyncRunner runner, CancellationToken ct) =>
{
    var result = await runner.RunAsync(ct, skipSettleTime: true);

    return result.Outcome switch
    {
        "busy" => Results.Json(result, statusCode: StatusCodes.Status409Conflict),
        "failed" => Results.Json(result, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.Ok(result),
    };
});

// Tests typed in on the page: any reading, at any time.
app.MapPost("/tests", async (ManualTest request, SyncRunner runner, CancellationToken ct) =>
{
    var result = await runner.RecordManualTestAsync(request, ct);

    return result.Outcome switch
    {
        "invalid" => Results.Json(result, statusCode: StatusCodes.Status400BadRequest),
        "failed" => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError),
        _ => Results.Ok(result),
    };
});

// A water body's test history, newest first.
app.MapGet("/tests", async (string waterBody, int? limit, PoolDatabase database, CancellationToken ct) =>
    Results.Ok(await database.TestsAsync(waterBody, Math.Clamp(limit ?? 50, 1, 5000), ct)));

// Only hand-entered tests can be deleted; synced and imported ones would come straight back.
app.MapDelete("/tests/{id}", async (string id, PoolDatabase database, SyncRunner runner, CancellationToken ct) =>
{
    if (!await database.DeleteManualTestAsync(id, ct))
    {
        return Results.NotFound(new { error = "No hand-entered test with that id." });
    }

    await runner.RunAsync(ct);
    return Results.NoContent();
});

// Copies Pool Math's whole history into the local database. Repeatable: already-imported entries
// are skipped. Run it before cancelling the subscription.
app.MapPost("/import/poolmath", async (
    PoolMathImporter importer, IOptions<PoolMathOptions> poolMath, SyncRunner runner, CancellationToken ct) =>
{
    if (!poolMath.Value.Enabled)
    {
        return Results.Json(new { error = "Pool Math is turned off (PoolMath:Enabled=false)." }, statusCode: 409);
    }

    var result = await importer.ImportAsync(ct);
    await runner.RunAsync(ct);
    return Results.Ok(result);
});

// Pool settings, edited on the page. Replaces the whole set for that water body.
app.MapPut("/settings/{waterBody}", async (
    string waterBody, PoolSettings settings, IOptions<List<WaterBodyOptions>> waterBodies,
    PoolDatabase database, SyncRunner runner, CancellationToken ct) =>
{
    var body = waterBodies.Value.FirstOrDefault(
        w => w.Enabled && string.Equals(w.Name, waterBody, StringComparison.OrdinalIgnoreCase));
    if (body is null)
    {
        return Results.NotFound(new { error = $"No water body named {waterBody}." });
    }

    if (SettingsProblem(settings) is { } problem)
    {
        return Results.BadRequest(new { error = problem });
    }

    await database.SaveSettingsAsync(body.Name, settings, ct);
    await runner.RunAsync(ct);
    return Results.Ok(settings);
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

static string? SettingsProblem(PoolSettings s)
{
    if (s.Volume is <= 0 or > 10_000_000)
    {
        return "Volume must be a positive number.";
    }

    if (s.VolumeUnit is not (0 or 1) || s.TempUnits is not (0 or 1))
    {
        return "Units must be 0 or 1.";
    }

    static bool Ordered(double? min, double? target, double? max) =>
        (min is null || target is null || min <= target) && (target is null || max is null || target <= max)
        && (min is null || max is null || min <= max);

    if (!Ordered(s.SaltMin, s.SaltTarget, s.SaltMax) || s.SaltTarget is < 0 or > 10000)
    {
        return "Salt min, target and max must be in order, within 0–10000.";
    }

    if (!Ordered(s.BorMin, s.BorTarget, s.BorMax) || s.BorTarget is < 0 or > 100)
    {
        return "Borate min, target and max must be in order, within 0–100.";
    }

    return s.FcTarget is < 0 or > 40 ? "FC target must be within 0–40." : null;
}
