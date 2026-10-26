using Application.AI.Common.Interfaces.Agent;
using Azure;
using Azure.AI.ContentSafety;
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
/// </para>
/// <para>
/// <b>When the service fails</b> (unreachable, throttled, a rejected credential, or a response that does not score
/// every requested category) the text is blocked by default
/// (<see cref="ContentSafetyConfig.BlockWhenUnavailable"/>): a screen that lets everything through while it is down is no
/// screen. The block reason says the screening was unavailable rather than naming a category, and the failure is logged
/// without the text.
/// </para>
/// </remarks>
public sealed class AzureContentSafetyService : ITextContentSafetyService
{
    /// <summary>The longest chunk sent in one call, in UTF-16 units: under the service's 10,000-character limit.</summary>
    internal const int MaxChunkLength = 9_000;

    /// <summary>How many characters consecutive chunks share, so text cut at a boundary is also screened whole.</summary>
    internal const int ChunkOverlap = 1_000;

    /// <summary>The block reason when screening could not run.</summary>
    internal const string UnavailableReason = "Content could not be screened: the safety service is unavailable.";

    /// <summary>The category recorded for a block because screening could not run.</summary>
    internal const string UnavailableCategory = "screening_unavailable";

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
            var result = await ScreenChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (result.IsBlocked)
                return result;
        }

        return new ContentSafetyResult(IsBlocked: false, BlockReason: null, Category: null);
    }

    private async Task<ContentSafetyResult> ScreenChunkAsync(string chunk, CancellationToken cancellationToken)
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
            return Unavailable(ex, ex.Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Unavailable(ex, status: null);
        }

        // A category that was asked for and came back unscored, or scored without a severity, was not screened.
        if (_categories.Any(requested => !scores.Any(score => score.Category == requested && score.Severity is not null)))
            return Unavailable(exception: null, status: null);

        var worst = scores
            .Where(score => score.Severity >= _config.BlockAtSeverity)
            .MaxBy(score => score.Severity);
        if (worst is null)
            return new ContentSafetyResult(IsBlocked: false, BlockReason: null, Category: null);

        _logger.LogInformation(
            "Content safety blocked a request: {Category} at severity {Severity} (threshold {Threshold})",
            worst.Category.ToString(), worst.Severity, _config.BlockAtSeverity);
        return new ContentSafetyResult(
            IsBlocked: true,
            BlockReason: $"Content blocked by safety policy ({worst.Category}).",
            Category: worst.Category.ToString());
    }

    // exception is null for a response that arrived but did not score every requested category.
    private ContentSafetyResult Unavailable(Exception? exception, int? status)
    {
        _logger.LogError(exception,
            "Content safety screening failed (status {Status}, {Failure}); the request is {Outcome}",
            status,
            exception is null ? "incomplete response" : exception.GetType().Name,
            _config.BlockWhenUnavailable ? "blocked" : "allowed unscreened");
        return _config.BlockWhenUnavailable
            ? new ContentSafetyResult(IsBlocked: true, BlockReason: UnavailableReason, Category: UnavailableCategory)
            : new ContentSafetyResult(IsBlocked: false, BlockReason: null, Category: null);
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
