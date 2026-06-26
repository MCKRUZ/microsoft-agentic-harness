using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Interfaces.WorkMemory;
using Application.AI.Common.Json;
using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Domain.AI.Learnings;
using Domain.AI.Prompts;
using Domain.AI.WorkMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.WorkMemory;

/// <summary>
/// Distills batches of <see cref="WorkEpisode"/> records into reusable <see cref="SynthesizedLesson"/>
/// proposals using an economy-tier LLM. The synthesis prompt is resolved from the versioned
/// <see cref="IPromptRegistry"/> (<c>work-episode-synthesizer</c>) and rendered with
/// <see cref="IPromptRenderer"/>, mirroring <c>ConversationFactExtractor</c> so trace-replay can
/// recover which prompt version produced each lesson set. Catches all expected failures internally —
/// callers always receive a valid (possibly empty) list.
/// </summary>
public sealed class LlmWorkEpisodeSynthesizer : IWorkEpisodeSynthesizer
{
    private const string PromptName = "work-episode-synthesizer";
    private const string OperationName = "work_episode_synthesis";
    private const string MetricKey = "work_episode_synthesis";
    private const int UserMessageTruncationLimit = 500;
    private const int ResponseSummaryTruncationLimit = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IModelRouter _modelRouter;
    private readonly IPromptRegistry _promptRegistry;
    private readonly IPromptRenderer _promptRenderer;
    private readonly IPromptUsageRecorder _usageRecorder;
    private readonly ILogger<LlmWorkEpisodeSynthesizer> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmWorkEpisodeSynthesizer"/> class.</summary>
    /// <param name="modelRouter">Routes the synthesis call to the configured model tier.</param>
    /// <param name="promptRegistry">Versioned prompt registry; resolves the synthesizer template.</param>
    /// <param name="promptRenderer">Renders the resolved template with variable substitution (Scriban).</param>
    /// <param name="usageRecorder">Stamps OTel / persists which prompt version was used per pass.</param>
    /// <param name="logger">Logger for recording synthesis results and failures.</param>
    public LlmWorkEpisodeSynthesizer(
        IModelRouter modelRouter,
        IPromptRegistry promptRegistry,
        IPromptRenderer promptRenderer,
        IPromptUsageRecorder usageRecorder,
        ILogger<LlmWorkEpisodeSynthesizer> logger)
    {
        ArgumentNullException.ThrowIfNull(modelRouter);
        ArgumentNullException.ThrowIfNull(promptRegistry);
        ArgumentNullException.ThrowIfNull(promptRenderer);
        ArgumentNullException.ThrowIfNull(usageRecorder);
        ArgumentNullException.ThrowIfNull(logger);

        _modelRouter = modelRouter;
        _promptRegistry = promptRegistry;
        _promptRenderer = promptRenderer;
        _usageRecorder = usageRecorder;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SynthesizedLesson>> SynthesizeAsync(
        IReadOnlyList<WorkEpisode> episodes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        if (episodes.Count == 0)
            return [];

        PromptDescriptor descriptor;
        try
        {
            descriptor = await _promptRegistry.GetLatestAsync(PromptName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or PromptRegistryUnavailableException)
        {
            _logger.LogWarning(ex,
                "Could not resolve prompt '{Prompt}'; synthesizing no lessons from {Count} episodes.",
                PromptName, episodes.Count);
            return [];
        }

        try
        {
            var variables = new Dictionary<string, object?>
            {
                ["episodes"] = FormatEpisodes(episodes),
            };
            var rendered = await _promptRenderer.RenderAsync(descriptor, variables, ct).ConfigureAwait(false);

            await _usageRecorder.RecordAsync(
                descriptor,
                new PromptUsageContext
                {
                    CaseId = string.Create(CultureInfo.InvariantCulture, $"work-synthesis:{episodes.Count}"),
                    MetricKey = MetricKey,
                },
                ct).ConfigureAwait(false);

            var client = (await _modelRouter.RouteOperationAsync(OperationName, ct)).Client;
            var response = await client.GetResponseAsync(rendered.Body, cancellationToken: ct);

            var lessons = ParseLessons(response.Text ?? "[]");

            _logger.LogDebug(
                "Synthesized {LessonCount} lessons from {EpisodeCount} episodes",
                lessons.Count, episodes.Count);

            return lessons;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Work-episode synthesis failed for a batch of {Count} episodes", episodes.Count);
            return [];
        }
    }

    /// <summary>
    /// Renders the episode batch into a compact, bounded textual block for the prompt. Each line is
    /// truncated so a large batch cannot blow the model's context window.
    /// </summary>
    private static string FormatEpisodes(IReadOnlyList<WorkEpisode> episodes)
    {
        var sb = new StringBuilder();
        var index = 1;
        foreach (var e in episodes)
        {
            var user = Truncate(e.UserMessage, UserMessageTruncationLimit);
            var summary = Truncate(e.ResponseSummary, ResponseSummaryTruncationLimit);
            sb.Append(CultureInfo.InvariantCulture, $"Episode {index}: outcome={e.Outcome}, tokens={e.TotalTokens}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  request: {user}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  response: {summary}\n");
            index++;
        }
        return sb.ToString();
    }

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit];

    private static IReadOnlyList<SynthesizedLesson> ParseLessons(string json)
    {
        if (!LlmJsonResponseParser.TryParseArray<List<RawLesson>>(json, JsonOptions, out var rawLessons)
            || rawLessons is null or { Count: 0 })
        {
            return [];
        }

        var lessons = new List<SynthesizedLesson>(rawLessons.Count);
        foreach (var raw in rawLessons)
        {
            if (string.IsNullOrWhiteSpace(raw.Content))
                continue;
            if (!Enum.TryParse<LearningCategory>(raw.Category, ignoreCase: true, out var category))
                continue;

            lessons.Add(new SynthesizedLesson
            {
                Content = raw.Content,
                Category = category,
                Confidence = Math.Clamp(raw.Confidence, 0d, 1d)
            });
        }

        return lessons;
    }

    private sealed record RawLesson
    {
        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;

        [JsonPropertyName("category")]
        public string? Category { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }
    }
}
