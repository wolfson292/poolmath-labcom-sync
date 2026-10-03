using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PoolSync.Configuration;

namespace PoolSync.Storage;

/// <summary>Where a test result came from. Only tests are trusted for chemistry; see <see cref="TestRecord"/>.</summary>
public static class TestSource
{
    /// <summary>A PoolLab session read from LabCOM.</summary>
    public const string LabCom = "labcom";

    /// <summary>Typed in on the status page.</summary>
    public const string Manual = "manual";

    /// <summary>Imported from Pool Math's history.</summary>
    public const string PoolMath = "poolmath";
}

/// <summary>
/// One water test. These are the high-accuracy readings that drive CSI and dosing; live sensor data,
/// which can drift, is deliberately kept out of this table.
/// </summary>
public sealed record TestRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString();

    public required string WaterBody { get; init; }

    public required DateTimeOffset TakenAt { get; init; }

    public required string Source { get; init; }

    public double? Fc { get; init; }

    public double? Cc { get; init; }

    public double? Ph { get; init; }

    public double? Ta { get; init; }

    public double? Cya { get; init; }

    public double? Ch { get; init; }

    public double? Salt { get; init; }

    public double? Bor { get; init; }

    public double? Tds { get; init; }

    public double? WaterTemp { get; init; }

    /// <summary>0 = Fahrenheit, 1 = Celsius, as Pool Math records it.</summary>
    public int? WaterTempUnits { get; init; }

    public string? Notes { get; init; }

    /// <summary>The weather at the time, as raw JSON, when the source recorded it.</summary>
    public string? Weather { get; init; }

    /// <summary>The source's own id, so importing or syncing the same test twice stores it once.</summary>
    public string? ExternalId { get; init; }

    public bool HasAnyReading =>
        Fc is not null || Cc is not null || Ph is not null || Ta is not null || Cya is not null
        || Ch is not null || Salt is not null || Bor is not null || Tds is not null || WaterTemp is not null;
}

/// <summary>A chemical added to the water.</summary>
public sealed record AdditionRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString();

    public required string WaterBody { get; init; }

    public required DateTimeOffset At { get; init; }

    public required string Source { get; init; }

    /// <summary>Readable product name, or null when only Pool Math's code is known.</summary>
    public string? Chemical { get; init; }

    /// <summary>Pool Math's chemical code, kept so imported entries can be named later.</summary>
    public int? ChemicalCode { get; init; }

    public double? Amount { get; init; }

    public string? Unit { get; init; }

    public int? UnitCode { get; init; }

    /// <summary>Strength, for products sold at more than one (liquid chlorine, acid).</summary>
    public double? Percent { get; init; }

    /// <summary>The amount in mL (liquids) or g (solids), when the unit is known.</summary>
    public double? Normalized { get; init; }

    public string? Notes { get; init; }

    public string? ExternalId { get; init; }
}

/// <summary>A maintenance entry: backwash, vacuum, filter pressure and the like, held as JSON fields.</summary>
public sealed record MaintenanceRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString();

    public required string WaterBody { get; init; }

    public required DateTimeOffset At { get; init; }

    public required string Source { get; init; }

    public required string Data { get; init; }

    public string? Notes { get; init; }

    public string? ExternalId { get; init; }
}

/// <summary>
/// The service's own record of every test, addition and maintenance entry, plus each pool's
/// settings. SQLite in the data volume: one small file, no server, and it survives Pool Math going
/// away.
/// </summary>
public sealed class PoolDatabase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public PoolDatabase(IOptions<SyncOptions> sync)
        : this(sync.Value.DatabasePath)
    {
    }

    public PoolDatabase(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public async Task<bool> InsertTestAsync(TestRecord test, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO tests
                (id, water_body, taken_at, source, fc, cc, ph, ta, cya, ch, salt, bor, tds,
                 water_temp, water_temp_units, notes, weather, external_id, created_at)
            VALUES
                ($id, $body, $at, $source, $fc, $cc, $ph, $ta, $cya, $ch, $salt, $bor, $tds,
                 $temp, $units, $notes, $weather, $external, $created)
            """;
        Add(command, "$id", test.Id);
        Add(command, "$body", test.WaterBody);
        Add(command, "$at", Format(test.TakenAt));
        Add(command, "$source", test.Source);
        Add(command, "$fc", test.Fc);
        Add(command, "$cc", test.Cc);
        Add(command, "$ph", test.Ph);
        Add(command, "$ta", test.Ta);
        Add(command, "$cya", test.Cya);
        Add(command, "$ch", test.Ch);
        Add(command, "$salt", test.Salt);
        Add(command, "$bor", test.Bor);
        Add(command, "$tds", test.Tds);
        Add(command, "$temp", test.WaterTemp);
        Add(command, "$units", test.WaterTempUnits);
        Add(command, "$notes", test.Notes);
        Add(command, "$weather", test.Weather);
        Add(command, "$external", test.ExternalId);
        Add(command, "$created", Format(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Tests for one water body, newest first.</summary>
    public async Task<IReadOnlyList<TestRecord>> TestsAsync(string waterBody, int? limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, water_body, taken_at, source, fc, cc, ph, ta, cya, ch, salt, bor, tds,
                   water_temp, water_temp_units, notes, weather, external_id
            FROM tests WHERE water_body = $body
            ORDER BY taken_at DESC, created_at DESC
            LIMIT $limit
            """;
        Add(command, "$body", waterBody);
        Add(command, "$limit", limit ?? -1);

        var tests = new List<TestRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tests.Add(new TestRecord
            {
                Id = reader.GetString(0),
                WaterBody = reader.GetString(1),
                TakenAt = Parse(reader.GetString(2)),
                Source = reader.GetString(3),
                Fc = Double(reader, 4),
                Cc = Double(reader, 5),
                Ph = Double(reader, 6),
                Ta = Double(reader, 7),
                Cya = Double(reader, 8),
                Ch = Double(reader, 9),
                Salt = Double(reader, 10),
                Bor = Double(reader, 11),
                Tds = Double(reader, 12),
                WaterTemp = Double(reader, 13),
                WaterTempUnits = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                Notes = String(reader, 15),
                Weather = String(reader, 16),
                ExternalId = String(reader, 17),
            });
        }

        return tests;
    }

    /// <summary>
    /// Deletes a hand-entered test. Imported and synced tests can't be deleted here: the next import
    /// or sync would only bring them back.
    /// </summary>
    public async Task<bool> DeleteManualTestAsync(string id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tests WHERE id = $id AND source = $source";
        Add(command, "$id", id);
        Add(command, "$source", TestSource.Manual);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> InsertAdditionAsync(AdditionRecord addition, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO additions
                (id, water_body, at, source, chemical, chemical_code, amount, unit, unit_code,
                 percent, normalized, notes, external_id, created_at)
            VALUES
                ($id, $body, $at, $source, $chemical, $code, $amount, $unit, $unitCode,
                 $percent, $normalized, $notes, $external, $created)
            """;
        Add(command, "$id", addition.Id);
        Add(command, "$body", addition.WaterBody);
        Add(command, "$at", Format(addition.At));
        Add(command, "$source", addition.Source);
        Add(command, "$chemical", addition.Chemical);
        Add(command, "$code", addition.ChemicalCode);
        Add(command, "$amount", addition.Amount);
        Add(command, "$unit", addition.Unit);
        Add(command, "$unitCode", addition.UnitCode);
        Add(command, "$percent", addition.Percent);
        Add(command, "$normalized", addition.Normalized);
        Add(command, "$notes", addition.Notes);
        Add(command, "$external", addition.ExternalId);
        Add(command, "$created", Format(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> InsertMaintenanceAsync(MaintenanceRecord entry, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO maintenance (id, water_body, at, source, data, notes, external_id, created_at)
            VALUES ($id, $body, $at, $source, $data, $notes, $external, $created)
            """;
        Add(command, "$id", entry.Id);
        Add(command, "$body", entry.WaterBody);
        Add(command, "$at", Format(entry.At));
        Add(command, "$source", entry.Source);
        Add(command, "$data", entry.Data);
        Add(command, "$notes", entry.Notes);
        Add(command, "$external", entry.ExternalId);
        Add(command, "$created", Format(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<PoolSettings?> SettingsAsync(string waterBody, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM pool_settings WHERE water_body = $body";
        Add(command, "$body", waterBody);
        return await command.ExecuteScalarAsync(ct) is string json
            ? JsonSerializer.Deserialize<PoolSettings>(json, Json)
            : null;
    }

    public async Task SaveSettingsAsync(string waterBody, PoolSettings settings, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pool_settings (water_body, json, updated_at) VALUES ($body, $json, $updated)
            ON CONFLICT (water_body) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
            """;
        Add(command, "$body", waterBody);
        Add(command, "$json", JsonSerializer.Serialize(settings, Json));
        Add(command, "$updated", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Row counts per table and source, for the status page footer and import results.</summary>
    public async Task<IReadOnlyDictionary<string, long>> CountsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'tests:' || source, COUNT(*) FROM tests GROUP BY source
            UNION ALL SELECT 'additions', COUNT(*) FROM additions
            UNION ALL SELECT 'maintenance', COUNT(*) FROM maintenance
            """;

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        if (!_schemaReady)
        {
            await _schemaGate.WaitAsync(ct);
            try
            {
                if (!_schemaReady)
                {
                    await CreateSchemaAsync(connection, ct);
                    _schemaReady = true;
                }
            }
            finally
            {
                _schemaGate.Release();
            }
        }

        return connection;
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();

        // Timestamps are ISO 8601 UTC text, which sorts chronologically as a string.
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS tests (
                id TEXT PRIMARY KEY,
                water_body TEXT NOT NULL,
                taken_at TEXT NOT NULL,
                source TEXT NOT NULL,
                fc REAL, cc REAL, ph REAL, ta REAL, cya REAL, ch REAL, salt REAL, bor REAL, tds REAL,
                water_temp REAL, water_temp_units INTEGER,
                notes TEXT,
                weather TEXT,
                external_id TEXT UNIQUE,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS tests_by_body_time ON tests (water_body, taken_at);

            CREATE TABLE IF NOT EXISTS additions (
                id TEXT PRIMARY KEY,
                water_body TEXT NOT NULL,
                at TEXT NOT NULL,
                source TEXT NOT NULL,
                chemical TEXT, chemical_code INTEGER,
                amount REAL, unit TEXT, unit_code INTEGER,
                percent REAL, normalized REAL,
                notes TEXT,
                external_id TEXT UNIQUE,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS additions_by_body_time ON additions (water_body, at);

            CREATE TABLE IF NOT EXISTS maintenance (
                id TEXT PRIMARY KEY,
                water_body TEXT NOT NULL,
                at TEXT NOT NULL,
                source TEXT NOT NULL,
                data TEXT NOT NULL,
                notes TEXT,
                external_id TEXT UNIQUE,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS maintenance_by_body_time ON maintenance (water_body, at);

            CREATE TABLE IF NOT EXISTS pool_settings (
                water_body TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static double? Double(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static string? String(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
