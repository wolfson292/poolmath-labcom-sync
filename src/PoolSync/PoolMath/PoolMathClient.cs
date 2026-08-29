using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PoolSync.Configuration;

namespace PoolSync.PoolMath;

/// <summary>
/// Client for the Pool Math API at api.poolmathapp.com.
///
/// Pool Math offers no API-key API, so this speaks the same protocol as the first-party apps:
/// POST /auth exchanges a Trouble Free Pool username and password for a token, which is then
/// presented as Basic auth over base64("{userId}:{token}"). Routes, headers and payloads match
/// requests captured from the official web client.
/// </summary>
public sealed class PoolMathClient : IPoolMathClient
{
    private readonly HttpClient _http;
    private readonly PoolMathOptions _options;
    private readonly ILogger<PoolMathClient> _logger;
    private readonly SemaphoreSlim _authGate = new(1, 1);

    private PoolMathCredentials? _credentials;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PoolMathClient(
        HttpClient http,
        IOptions<PoolMathOptions> options,
        ILogger<PoolMathClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        _http.BaseAddress = new Uri(
            _options.ApiServer.EndsWith('/') ? _options.ApiServer : _options.ApiServer + "/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("x-clientversion", _options.ClientVersion);

        if (!string.IsNullOrWhiteSpace(_options.UserId) && !string.IsNullOrWhiteSpace(_options.AuthToken))
        {
            _credentials = new PoolMathCredentials(_options.UserId!, _options.AuthToken!);
        }
    }

    public async Task<IReadOnlyList<PoolMathPool>> ListPoolsAsync(CancellationToken ct)
    {
        // /pools/list takes no body; the account's pools all come back in one page.
        using var response = await SendAuthenticatedAsync(_options.PoolsListRoute, content: null, ct);
        var payload = await ReadAsync<PagedResults<PoolMathPool>>(response, ct);

        return payload?.Results?.Where(p => !p.Deleted).ToList() ?? [];
    }

    public async Task PushTestLogsAsync(IReadOnlyList<PoolMathTestLog> logs, CancellationToken ct)
    {
        if (logs.Count == 0)
        {
            return;
        }

        // There is no batch route: /testlogs stores one document per call.
        foreach (var log in logs)
        {
            log.Origin ??= _options.Origin;

            using var content = JsonContent.Create(log, options: JsonOptions);
            using var response = await SendAuthenticatedAsync(_options.TestLogRoute, content, ct);
            var stored = await ReadAsync<TestLogResponse>(response, ct);

            _logger.LogInformation(
                "Pool Math stored test log {LogId} for pool {PoolId} at {Timestamp}.",
                stored?.Log?.Id ?? "(no id returned)",
                log.PoolId,
                log.LogTimestamp);

            // The server assigns the id, so surface it for the state file.
            log.Id = stored?.Log?.Id;
        }
    }

    /// <summary>
    /// Signs in and returns the credentials, so a token can be issued once on a trusted machine and
    /// deployed instead of the account password.
    /// </summary>
    public Task<PoolMathCredentials> AuthenticateAsync(CancellationToken ct) =>
        EnsureAuthenticatedAsync(ct);

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        string route, HttpContent? content, CancellationToken ct)
    {
        var credentials = await EnsureAuthenticatedAsync(ct);
        var response = await PostAsync(route, content, credentials, ct);

        // A revoked or expired token is worth one retry; everything else is surfaced to the caller.
        if (response.StatusCode == HttpStatusCode.Unauthorized && CanSignIn)
        {
            response.Dispose();
            _logger.LogInformation("Pool Math rejected the stored token; signing in again.");

            _credentials = null;
            credentials = await EnsureAuthenticatedAsync(ct);
            response = await PostAsync(route, content, credentials, ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new PoolMathException($"Pool Math {route} returned {status}: {Truncate(body)}");
        }

        return response;
    }

    private Task<HttpResponseMessage> PostAsync(
        string route, HttpContent? content, PoolMathCredentials credentials, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = content,
            Headers = { Authorization = BasicHeader(credentials.UserId, credentials.AuthToken) },
        };

        return _http.SendAsync(request, ct);
    }

    private bool CanSignIn =>
        !string.IsNullOrWhiteSpace(_options.Username) && !string.IsNullOrWhiteSpace(_options.Password);

    private async Task<PoolMathCredentials> EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_credentials is not null)
        {
            return _credentials;
        }

        await _authGate.WaitAsync(ct);
        try
        {
            if (_credentials is not null)
            {
                return _credentials;
            }

            if (!CanSignIn)
            {
                throw new PoolMathException(
                    "No Pool Math credentials. Set PoolMath:UserId and PoolMath:AuthToken, " +
                    "or PoolMath:Username and PoolMath:Password.");
            }

            _credentials = await SignInAsync(ct);
            _logger.LogInformation("Signed in to Pool Math as {UserId}.", _credentials.UserId);
            return _credentials;
        }
        finally
        {
            _authGate.Release();
        }
    }

    private async Task<PoolMathCredentials> SignInAsync(CancellationToken ct)
    {
        // POST /auth is the one unauthenticated call: credentials go in the body, not a header.
        var payload = new
        {
            provider = "tfp",
            token = (string?)null,
            user = _options.Username,
            pwd = _options.Password,
            device = _options.DeviceName,
        };

        using var response = await _http.PostAsJsonAsync(_options.AuthRoute, payload, JsonOptions, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new PoolMathException(
                $"Pool Math sign-in returned {(int)response.StatusCode}. " +
                "Check PoolMath:Username (the TFP forum username, not the email) and PoolMath:Password.");
        }

        var user = JsonSerializer.Deserialize<PoolMathUser>(body, JsonOptions);
        var userId = user?.UserId;

        // Each sign-in adds an authorization named after this device; prefer ours over any others.
        var token = user?.Authorizations
            ?.FirstOrDefault(a => a.Name == _options.DeviceName)?.Token
            ?? user?.Authorizations?.LastOrDefault()?.Token;

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
        {
            throw new PoolMathException("Pool Math sign-in succeeded but returned no usable token.");
        }

        return new PoolMathCredentials(userId, token);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        }
    }

    private static AuthenticationHeaderValue BasicHeader(string userId, string token) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userId}:{token}")));

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";
}
