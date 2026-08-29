using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoolSync.LabCom;

public sealed class GraphQlResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("errors")]
    public List<GraphQlError>? Errors { get; set; }
}

public sealed class GraphQlError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class CloudAccountPayload
{
    [JsonPropertyName("CloudAccount")]
    public CloudAccount? CloudAccount { get; set; }
}

public sealed class CloudAccount
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("Accounts")]
    public List<LabComAccount> Accounts { get; set; } = [];
}

/// <summary>A LabCOM "account" is one water body: the app files measurements under these.</summary>
public sealed class LabComAccount
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("forename")]
    public string? Forename { get; set; }

    [JsonPropertyName("surname")]
    public string? Surname { get; set; }

    [JsonPropertyName("pooltext")]
    public string? PoolText { get; set; }

    [JsonPropertyName("volume")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Volume { get; set; }

    [JsonPropertyName("volume_unit")]
    public string? VolumeUnit { get; set; }

    [JsonPropertyName("Measurements")]
    public List<LabComMeasurement> Measurements { get; set; } = [];

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(PoolText) ? PoolText!
        : $"{Forename} {Surname}".Trim() is { Length: > 0 } n ? n
        : $"account {Id}";
}

public sealed class LabComMeasurement
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("scenario")]
    public string? Scenario { get; set; }

    [JsonPropertyName("parameter")]
    public string? Parameter { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("device_serial")]
    public string? DeviceSerial { get; set; }

    [JsonPropertyName("operator_name")]
    public string? OperatorName { get; set; }

    [JsonPropertyName("value")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Value { get; set; }

    [JsonPropertyName("formatted_value")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? FormattedValue { get; set; }

    [JsonPropertyName("timestamp")]
    [JsonConverter(typeof(EpochSecondsConverter))]
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The numeric reading, or null when LabCOM returned a non-numeric value.</summary>
    public double? NumericValue =>
        double.TryParse(Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}

/// <summary>LabCOM returns some scalars as strings and others as numbers depending on the field.</summary>
public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => reader.GetString(),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>Measurement timestamps come back as Unix seconds, occasionally quoted.</summary>
public sealed class EpochSecondsConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        long seconds = reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt64(),
            JsonTokenType.String => long.TryParse(reader.GetString(), out var s) ? s : 0,
            _ => 0,
        };
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.ToUnixTimeSeconds());
}
