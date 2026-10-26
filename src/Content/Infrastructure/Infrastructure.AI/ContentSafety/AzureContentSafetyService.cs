using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Agent;
using Azure;
using Azure.AI.ContentSafety;
using Azure.Identity;
using Domain.AI.Constants;
using Domain.AI.Models;
using Domain.Common.Config.AI.ContentSafety;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.ContentSafety;

/// <summary>
/// Screens request text with Azure AI Content Safety (#904): blocks it when any configured category reaches
/// <see cref="ContentSafetyConfig.BlockAtSeverity"/>.
/// </summary>
/// <remarks>
/// <para>
/// The service analyses at most 10,000 characters per call, so longer text is screened in chunks that overlap by
/// <see cref="ChunkOverlap"/> characters, cut on a boundary that never splits a surrogate pair, and screening stops at
/// the first chunk that blocks. The overlap matters: with back-to-back chunks, padding a message so a harmful sentence
/// straddles a cut gets each half scored without the other. Any passage up to the overlap long lies whole in one chunk.
/// Chunks are screened one after another, not concurrently: a long message already makes several calls, and sending
/// them at once is what trips the service's per-second rate limit on the lower tiers.
/// </para>
/// <para>
/// <b>When screening cannot run</b> (the service is unreachable, throttled or timed out, the credential cannot be
/// obtained or is rejected, or a response does not score every requested category), that is not a verdict on the
/// text, so it is never reported as a block. By default (<see cref="ContentSafetyConfig.BlockWhenUnavailable"/>) the
/// call throws <see cref="ContentSafetyUnavailableException"/>, which surfaces as a service failure the caller can
/// retry; a deployment that allows unscreened content gets a pass for the whole text instead, without calling the
/// service again for the rest of it. Either way the failure is logged, without the text. Any other exception is a bug,
/// not an outage, and propagates as it is.
/// </para>
/// <para>
/// A block's category is reported as the harness's own name for it (<see cref="SafetyCategories"/>), so dashboards and
/// callers see the same value whichever provider screened.
/// </para>
/// </remarks>
public sealed class AzureContentSafetyService : ITextContentSafetyService
{
    /// <summary>The longest chunk sent in one call, in UTF-16 units: under the service's 10,000-character limit.</summary>
    internal const int MaxChunkLength = 9_000;

    /// <summary>How many characters consecutive chunks share, so text cut at a boundary is also screened whole.</summary>
    internal const int ChunkOverlap = 1_000;

    private static readonly ContentSafetyResult Pass = new(IsBlocked: false, BlockReason: null, Category: null);

    private readonly ContentSafetyClient _client;
    private readonly ContentSafetyConfig _config;
    private readonly IReadOnlyList<TextCategory> _categories;
    private readonly ILogger<AzureContentSafetyService> _logger;

    /// <summary>Initializes a new instance of the <see cref="AzureContentSafetyService"/> class.</summary>
    /// <param name="client">The Content Safety client, already pointed at the configured endpoint and credential.</param>
    /// <param name="config">The screening settings.</param>
    /// <param name="logger">Logger for blocks and service failures; never logs the screened text.</param>
    public AzureContentSafetyService(
        ContentSafetyClient client,
        ContentSafetyConfig config,
        ILogger<AzureContentSafetyService> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _config = config;
        _categories = [.. config.ScreenedCategories.Select(name => new TextCategory(name))];
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ContentSafetyResult> ScreenAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        foreach (var chunk in Chunks(content))
        {
            var (result, finished) = await ScreenChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (finished)
                return result;
        }

        return Pass;
    }

    // Finished is true when the outcome for the whole text is decided: a block, or unscreened content let through.
    private async Task<(ContentSafetyResult Result, bool Finished)> ScreenChunkAsync(
        string chunk, CancellationToken cancellationToken)
    {
        IReadOnlyList<TextCategoriesAnalysis> scores;
        try
        {
            var options = new AnalyzeTextOptions(chunk);
            foreach (var category in _categories)
                options.Categories.Add(category);
            var analysis = (await _client.AnalyzeTextAsync(options, cancellationToken).ConfigureAwait(false)).Value;
            scores = [.. analysis?.CategoriesAnalysis ?? []];
        }
        catch (RequestFailedException ex)
        {
            return (Unavailable($"status {ex.Status}", ex), true);
        }
        catch (AuthenticationFailedException ex)
        {
            return (Unavailable("credential unavailable", ex), true);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (Unavailable("timed out", ex), true);
        }

        // A category that was asked for and came back unscored, or scored without a severity, was not screened.
        if (_categories.Any(requested => !scores.Any(score => score.Category == requested && score.Severity is not null)))
            return (Unavailable("incomplete analysis", exception: null), true);

        var worst = scores
            .Where(score => score.Severity >= _config.BlockAtSeverity)
            .MaxBy(score => score.Severity);
        if (worst is null)
            return (Pass, false);

        var blocked = HarnessCategory(worst.Category);
        _logger.LogInformation(
            "Content safety blocked a request: {Category} at severity {Severity} (threshold {Threshold})",
            blocked, worst.Severity, _config.BlockAtSeverity);
        return (new ContentSafetyResult(
            IsBlocked: true,
            BlockReason: $"Content blocked by safety policy ({blocked}).",
            Category: blocked), true);
    }

    // Throws when unscreened content is blocked; otherwise lets the whole text through unscreened.
    private ContentSafetyResult Unavailable(string failure, Exception? exception)
    {
        _logger.LogError(exception,
            "Content safety screening failed ({Failure}); the request is {Outcome}",
            failure, _config.BlockWhenUnavailable ? "refused" : "allowed unscreened");
        if (_config.BlockWhenUnavailable)
            throw new ContentSafetyUnavailableException(
                $"Content could not be screened: the safety service is unavailable ({failure}).", exception);
        return Pass;
    }

    internal static string HarnessCategory(TextCategory category)
    {
        if (category == TextCategory.Hate)
            return SafetyCategories.Hate;
        if (category == TextCategory.SelfHarm)
            return SafetyCategories.SelfHarm;
        if (category == TextCategory.Sexual)
            return SafetyCategories.Sexual;
        if (category == TextCategory.Violence)
            return SafetyCategories.Violence;
        return category.ToString().ToLowerInvariant();
    }

    // Pieces of at most MaxChunkLength units, each starting ChunkOverlap units before the previous one ended, never
    // ending or starting between the halves of a surrogate pair.
    internal static IEnumerable<string> Chunks(string content)
    {
        if (content.Length <= MaxChunkLength)
        {
            yield return content;
            yield break;
        }

        for (var start = 0; ;)
        {
            var length = Math.Min(MaxChunkLength, content.Length - start);
            if (start + length < content.Length && char.IsHighSurrogate(content[start + length - 1]))
                length--;
            yield return content.Substring(start, length);

            var end = start + length;
            if (end == content.Length)
                yield break;

            start = end - ChunkOverlap;
            if (char.IsLowSurrogate(content[start]))
                start--;
        }
    }
}
