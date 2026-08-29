using System.Text.Json;
using PoolSync.PoolMath;
using Xunit;

namespace PoolSync.Tests;

/// <summary>
/// Pool Math's API is undocumented, so the request has to keep matching what the official clients
/// send. These assertions are taken from a captured POST /testlogs.
/// </summary>
public class PoolMathTestLogSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static JsonElement Serialize(PoolMathTestLog log) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(log, Options));

    [Fact]
    public void Payload_carries_every_field_the_official_client_sends()
    {
        var json = Serialize(new PoolMathTestLog { PoolId = "pool-guid", Ph = 7.5 });

        string[] expected =
        [
            "type", "fc", "cc", "cya", "ch", "ph", "ta", "salt", "bor", "tds", "csi",
            "waterTemp", "waterTempUnits", "poolId", "logTimestamp", "weather",
            "weatherLogId", "userId", "origin", "id", "_ts", "deleted",
        ];

        foreach (var name in expected)
        {
            Assert.True(json.TryGetProperty(name, out _), $"payload is missing '{name}'");
        }
    }

    [Fact]
    public void Unmeasured_parameters_are_sent_as_null_rather_than_omitted()
    {
        var json = Serialize(new PoolMathTestLog { PoolId = "pool-guid", Ph = 7.5 });

        Assert.Equal(JsonValueKind.Null, json.GetProperty("fc").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("cya").ValueKind);
        Assert.Equal(7.5, json.GetProperty("ph").GetDouble());
    }

    [Fact]
    public void New_logs_leave_the_server_assigned_fields_unset()
    {
        var json = Serialize(new PoolMathTestLog { PoolId = "pool-guid" });

        Assert.Equal(JsonValueKind.Null, json.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("userId").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("weather").ValueKind);
        Assert.Equal(PoolMathTestLog.UnsetTimestamp, json.GetProperty("_ts").GetInt64());
        Assert.False(json.GetProperty("deleted").GetBoolean());
        Assert.Equal("testlog", json.GetProperty("type").GetString());
    }

    [Fact]
    public void Notes_are_only_sent_when_one_is_configured()
    {
        Assert.False(Serialize(new PoolMathTestLog()).TryGetProperty("notes", out _));
        Assert.True(Serialize(new PoolMathTestLog { Notes = "hi" }).TryGetProperty("notes", out _));
    }
}
