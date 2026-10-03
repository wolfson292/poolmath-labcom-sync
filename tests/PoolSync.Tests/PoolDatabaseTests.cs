using PoolSync.Configuration;
using PoolSync.Storage;
using Xunit;

namespace PoolSync.Tests;

public sealed class PoolDatabaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"poolsync-{Guid.NewGuid()}.db");
    private readonly PoolDatabase _database;

    public PoolDatabaseTests() => _database = new PoolDatabase(_path);

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    private static TestRecord Test(string? externalId = null, string source = TestSource.LabCom) => new()
    {
        WaterBody = "Allaire",
        TakenAt = new DateTimeOffset(2026, 10, 3, 17, 29, 9, TimeSpan.Zero),
        Source = source,
        Fc = 5.8,
        Ph = 7.6,
        WaterTemp = 84,
        WaterTempUnits = 0,
        ExternalId = externalId,
    };

    [Fact]
    public async Task A_test_round_trips()
    {
        var test = Test("labcom:2:484");
        await _database.InsertTestAsync(test, default);

        var stored = Assert.Single(await _database.TestsAsync("Allaire", null, default));

        Assert.Equal(test, stored);
    }

    [Fact]
    public async Task The_same_external_id_is_stored_once()
    {
        Assert.True(await _database.InsertTestAsync(Test("poolmath:abc"), default));
        Assert.False(await _database.InsertTestAsync(Test("poolmath:abc"), default));

        Assert.Single(await _database.TestsAsync("Allaire", null, default));
    }

    [Fact]
    public async Task Tests_come_back_newest_first_and_per_water_body()
    {
        await _database.InsertTestAsync(Test() with { TakenAt = Test().TakenAt.AddDays(-1), Ph = 7.2 }, default);
        await _database.InsertTestAsync(Test(), default);
        await _database.InsertTestAsync(Test() with { WaterBody = "SPA" }, default);

        var tests = await _database.TestsAsync("Allaire", null, default);

        Assert.Equal([7.6, 7.2], tests.Select(t => t.Ph!.Value));
    }

    [Fact]
    public async Task Only_hand_entered_tests_can_be_deleted()
    {
        var synced = Test("labcom:2:1");
        var manual = Test(source: TestSource.Manual);
        await _database.InsertTestAsync(synced, default);
        await _database.InsertTestAsync(manual, default);

        Assert.False(await _database.DeleteManualTestAsync(synced.Id, default));
        Assert.True(await _database.DeleteManualTestAsync(manual.Id, default));

        Assert.Equal(synced.Id, Assert.Single(await _database.TestsAsync("Allaire", null, default)).Id);
    }

    [Fact]
    public async Task Settings_round_trip_and_update()
    {
        Assert.Null(await _database.SettingsAsync("Allaire", default));

        var settings = new PoolSettings { Volume = 25000, Swg = true, SaltTarget = 3600, Surface = PoolSurface.Vinyl };
        await _database.SaveSettingsAsync("Allaire", settings, default);
        await _database.SaveSettingsAsync("Allaire", settings with { Volume = 24000 }, default);

        Assert.Equal(settings with { Volume = 24000 }, await _database.SettingsAsync("Allaire", default));
    }

    [Fact]
    public async Task Counts_are_reported_per_source()
    {
        await _database.InsertTestAsync(Test("a"), default);
        await _database.InsertTestAsync(Test("b", TestSource.PoolMath), default);
        await _database.InsertAdditionAsync(new AdditionRecord
        {
            WaterBody = "Allaire", At = DateTimeOffset.UtcNow, Source = TestSource.PoolMath, ExternalId = "x",
        }, default);

        var counts = await _database.CountsAsync(default);

        Assert.Equal(1, counts["tests:labcom"]);
        Assert.Equal(1, counts["tests:poolmath"]);
        Assert.Equal(1, counts["additions"]);
    }
}
