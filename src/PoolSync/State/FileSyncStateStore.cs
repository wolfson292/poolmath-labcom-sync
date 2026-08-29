using System.Text.Json;
using Microsoft.Extensions.Options;
using PoolSync.Configuration;

namespace PoolSync.State;

/// <summary>Persists sync state to a JSON file on a mounted volume.</summary>
public sealed class FileSyncStateStore(
    IOptions<SyncOptions> options,
    ILogger<FileSyncStateStore> logger) : ISyncStateStore
{
    private readonly string _path = options.Value.StatePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<SyncState> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path))
            {
                logger.LogInformation("No state file at {Path}; starting from the configured backfill.", _path);
                return new SyncState();
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<SyncState>(stream, JsonOptions, ct)
                ?? new SyncState();
        }
        catch (JsonException ex)
        {
            // A corrupt state file would otherwise wedge the service on every poll. Re-import from
            // the backfill window instead; Pool Math logs carry stable ids so this stays recoverable.
            logger.LogError(ex, "State file at {Path} is unreadable; starting fresh.", _path);
            return new SyncState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SyncState state, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-move so a crash mid-write can't truncate the existing state.
            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, ct);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
