using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PoolSync.Configuration;

namespace PoolSync.LabCom;

/// <summary>Reads measurements from the LabCOM Cloud GraphQL API.</summary>
public sealed class LabComClient(
    HttpClient http,
    IOptions<LabComOptions> options,
    ILogger<LabComClient> logger)
{
    private readonly LabComOptions _options = options.Value;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Mirrors the query the LabCOM apps issue: the whole cloud account in one round trip.
    private const string Query = """
        query {
          CloudAccount {
            id
            email
            Accounts {
              id
              forename
              surname
              pooltext
              volume
              volume_unit
              Measurements {
                id
                scenario
                parameter
                unit
                comment
                device_serial
                operator_name
                value
                formatted_value
                timestamp
              }
            }
          }
        }
        """;

    public async Task<CloudAccount> GetCloudAccountAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(new { query = Query }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", _options.ApiToken);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new LabComException(
                $"LabCOM returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}");
        }

        var payload = JsonSerializer.Deserialize<GraphQlResponse<CloudAccountPayload>>(body, JsonOptions)
            ?? throw new LabComException("LabCOM returned an empty response.");

        if (payload.Errors is { Count: > 0 })
        {
            throw new LabComException(
                "LabCOM returned errors: " + string.Join("; ", payload.Errors.Select(e => e.Message)));
        }

        var account = payload.Data?.CloudAccount
            ?? throw new LabComException("LabCOM response contained no CloudAccount.");

        logger.LogDebug(
            "Fetched {AccountCount} LabCOM account(s), {MeasurementCount} measurement(s) total.",
            account.Accounts.Count,
            account.Accounts.Sum(a => a.Measurements.Count));

        return account;
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";
}

public sealed class LabComException(string message) : Exception(message);
