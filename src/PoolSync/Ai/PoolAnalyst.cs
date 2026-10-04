using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;
using PoolSync.Storage;
using PoolSync.Sync;

namespace PoolSync.Ai;

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Anthropic API key. Falls back to ANTHROPIC_API_KEY when unset.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5-5";

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
}

/// <summary>A finished analysis, kept so the page shows the latest one after a reload.</summary>
public sealed record PoolAnalysis(string WaterBody, DateTimeOffset At, string Model, string Text);

public sealed class AnalysisUnavailableException(string message) : Exception(message);

/// <summary>
/// Asks Claude to read a water body's current state and recent history and say what to do next.
/// Everything it sees is gathered here from the service's own data; it has no tools and no other
/// source, so its advice can only rest on what the page itself shows.
/// </summary>
public sealed class PoolAnalyst(
    IOptions<AiOptions> options,
    PoolDatabase database,
    SyncStatus status,
    ILogger<PoolAnalyst> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string SystemPrompt = """
        You are a pool water chemistry advisor following the Trouble Free Pool (TFP) method. You'll get
        one water body's data as JSON: its settings, the current readings (each with its source and
        when it was measured), CSI and the dosing the page already recommends, recent tests, chemicals
        added, maintenance, rain dilution estimates, and what its pool controller's sensors report.

        Tests (LabCOM photometer, hand-entered, imported) are the record. Controller sensors drift, so
        treat them as supporting evidence and point out where they disagree with tests. Readings can be
        old: weigh how recent each one is, and say when a retest matters more than a dose.

        Write for the pool's owner, who knows TFP. Lead with the few things that matter most right now,
        in order, with amounts where the data supports them; then anything to watch or retest; then
        brief observations about trends or equipment if they're useful. Use short sections with simple
        Markdown (## headings, - bullets, **bold**). Base every number on the data given, and say so
        when the data is too thin or old to be sure. Don't repeat the whole dataset back.
        """;

    public async Task<PoolAnalysis?> LatestAsync(string waterBody, CancellationToken ct) =>
        await database.AnalysisAsync(waterBody, ct);

    public async Task<PoolAnalysis> AnalyzeAsync(string waterBody, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Configured)
        {
            throw new AnalysisUnavailableException(
                "No Anthropic API key. Set POOLSYNC_Ai__ApiKey (or ANTHROPIC_API_KEY) to enable analysis.");
        }

        if (!status.WaterBodies.TryGetValue(waterBody, out var current))
        {
            throw new AnalysisUnavailableException($"No status yet for {waterBody}; wait for the first sync.");
        }

        var context = await ContextAsync(current, ct);
        var client = new AnthropicClient
        {
            ApiKey = settings.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        };

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = settings.Model,
                MaxTokens = 16000,
                Thinking = new BetaThinkingConfigAdaptive(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.High },
                // A refusal is re-served by a fallback model in the same call rather than returning nothing.
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
                System = SystemPrompt,
                Messages =
                [
                    new()
                    {
                        Role = Role.User,
                        Content = $"Today is {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC. Analyse {waterBody}:\n\n{context}",
                    },
                ],
            }, ct);
        }
        catch (AnthropicRateLimitException)
        {
            throw new AnalysisUnavailableException("The Anthropic API is rate limiting requests; try again in a minute.");
        }
        catch (AnthropicApiException ex)
        {
            logger.LogWarning("Analysis of {WaterBody} failed: {Message}", waterBody, ex.Message);
            throw new AnalysisUnavailableException($"The Anthropic API returned an error: {ex.Message}");
        }

        if (response.StopReason == "refusal")
        {
            throw new AnalysisUnavailableException("The model declined to analyse this data.");
        }

        var text = string.Join("\n\n", response.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .OfType<string>());
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AnalysisUnavailableException("The analysis came back empty.");
        }

        var analysis = new PoolAnalysis(waterBody, DateTimeOffset.UtcNow, response.Model, text.Trim());
        await database.SaveAnalysisAsync(analysis, ct);
        logger.LogInformation(
            "Analysed {WaterBody} with {Model}: {Input} in, {Output} out.",
            waterBody, response.Model, response.Usage.InputTokens, response.Usage.OutputTokens);
        return analysis;
    }

    /// <summary>The water body's data as compact JSON: current state plus the recent history behind it.</summary>
    private async Task<string> ContextAsync(WaterBodyStatus current, CancellationToken ct)
    {
        var yearAgo = DateTimeOffset.UtcNow.AddDays(-365);
        var tests = (await database.TestsAsync(current.Name, 40, ct))
            .Select(t => new
            {
                t.TakenAt, t.Source, t.Fc, t.Cc, t.Ph, t.Ta, t.Cya, t.Ch, t.Salt, t.Bor,
                waterTemp = t.WaterTemp, waterTempUnit = t.WaterTemp is null ? null : t.WaterTempUnits == 1 ? "C" : "F",
                t.Notes,
            });
        var additions = (await database.AdditionsAsync(current.Name, 30, ct))
            .Where(a => a.At >= yearAgo)
            .Select(a => new { a.At, chemical = a.Chemical ?? $"unknown (Pool Math code {a.ChemicalCode})", a.Amount, a.Unit, a.Percent, a.Source });
        var maintenance = (await database.MaintenanceAsync(current.Name, 15, ct))
            .Select(m => new { m.At, data = JsonDocument.Parse(m.Data).RootElement, m.Notes });

        var document = new
        {
            waterBody = current.Name,
            settings = current.Settings,
            current = current.Balance is { } b
                ? new
                {
                    readings = b.Water.ToDictionary(
                        r => r.Key,
                        r => new { r.Value.Value, r.Value.Source, measuredAt = r.Value.At, note = r.Value.Note }),
                    waterTempNote = "waterTempC is in °C",
                    b.Csi,
                    csiAfterRecommendedChanges = b.CsiAfter,
                    idealRanges = b.Targets.Select(t => new { t.Label, t.Min, t.Max, t.Target, t.Status }),
                    recommendedDoses = b.Recommendations,
                    b.Notes,
                }
                : null,
            rainDilution = current.Dilution,
            controller = current.Controller,
            reminders = current.Reminders,
            recentTests = tests,
            recentChemicalsAdded = additions,
            recentMaintenance = maintenance,
        };

        return JsonSerializer.Serialize(document, Json);
    }
}
