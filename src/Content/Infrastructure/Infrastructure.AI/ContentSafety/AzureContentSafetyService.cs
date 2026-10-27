using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Resilience;
using Azure;
using Azure.AI.ContentSafety;
using Azure.Identity;
using Domain.AI.Constants;
using Domain.AI.Models;
using Domain.AI.Resilience;
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
/// <b>When screening cannot run</b> the call throws <see cref="ContentSafetyUnavailableException"/>, which is never a
/// block: an outage is not a verdict on the text. Whether the request is then refused or let through unscreened is
/// <c>ContentSafetyBehavior</c>'s decision. A failure counts as an outage when the harness's provider error classifier
/// (<see cref="IProviderErrorClassifier"/>) recognises it as a transport fault, a timeout or a service-reported error;
/// it unwraps the SDK's retry aggregate, so a host that cannot be reached is an outage after the retry as well as
/// before it. Any request the SDK itself reports as failed is an outage too, as are a credential that cannot be
/// obtained and a response that does not score every requested category. Anything else is a bug, not an outage, and
/// propagates as it is; so does the caller cancelling, even when a timeout raced the cancellation. The failure is
/// logged with the classifier's reason (a rejected key or a wrong endpoint reads as a misconfiguration there, not as
/// a passing outage) and without the text.
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
    private readonly int _blockAtSeverity;
    private readonly IProviderErrorClassifier _errors;
    private readonly IReadOnlyList<TextCategory> _categories;
    private readonly ILogger<AzureContentSafetyService> _logger;

    /// <summary>Initializes a new instance of the <see cref="AzureContentSafetyService"/> class.</summary>
    /// <param name="client">The Content Safety client, already pointed at the configured endpoint and credential.</param>
    /// <param name="config">The screening settings.</param>
    /// <param name="errors">Tells a service outage apart from a bug.</param>
    /// <param name="logger">Logger for blocks and service failures; never logs the screened text.</param>
    public AzureContentSafetyService(
        ContentSafetyClient client,
        ContentSafetyConfig config,
        IProviderErrorClassifier errors,
        ILogger<AzureContentSafetyService> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _blockAtSeverity = config.BlockAtSeverity;
        _errors = errors;
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

        return Pass;
    }

    private async Task<ContentSafetyResult> ScreenChunkAsync(string chunk, CancellationToken cancellationToken)
    {
        var scores = await AnalyzeAsync(chunk, cancellationToken).ConfigureAwait(false);

        // A category that was asked for and came back unscored, or scored without a severity, was not screened.
        if (_categories.Any(requested => !scores.Any(score => score.Category == requested && score.Severity is not null)))
            throw Unavailable("incomplete analysis", exception: null);

        var worst = scores
            .Where(score => score.Severity >= _blockAtSeverity)
            .MaxBy(score => score.Severity);
        if (worst is null)
            return Pass;

        var blocked = HarnessCategory(worst.Category);
        _logger.LogInformation(
            "Content safety blocked a request: {Category} at severity {Severity} (threshold {Threshold})",
            blocked, worst.Severity, _blockAtSeverity);
        return new ContentSafetyResult(
            IsBlocked: true,
            BlockReason: $"Content blocked by safety policy ({blocked}).",
            Category: blocked);
    }

    private async Task<IReadOnlyList<TextCategoriesAnalysis>> AnalyzeAsync(string chunk, CancellationToken cancellationToken)
    {
        try
        {
            var options = new AnalyzeTextOptions(chunk);
            foreach (var category in _categories)
                options.Categories.Add(category);
            var analysis = (await _client.AnalyzeTextAsync(options, cancellationToken).ConfigureAwait(false)).Value;
            return analysis?.CategoriesAnalysis ?? [];
        }
        catch (AuthenticationFailedException ex)
        {
            throw Unavailable("credential unavailable", ex);
        }
        catch (Exception ex) when (Outage(ex, cancellationToken) is { } failure)
        {
            throw Unavailable(failure, ex);
        }
    }

    // What to call the failure when it is the service's: one the classifier recognises, or any request the SDK reports
    // as failed (a RequestFailedException, whatever its status). Null for the caller cancelling, even when a timeout
    // raced it, and for anything else, which is a bug and propagates as it is.
    private string? Outage(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return null;

        var classification = _errors.Classify(exception, cancellationToken);
        if (classification.Kind is not (ProviderFailureKind.Unknown or ProviderFailureKind.CallerCancelled))
            return classification.ReasonCode ?? classification.Kind.ToString();
        return exception is RequestFailedException failed ? $"request failed, status {failed.Status}" : null;
    }

    private ContentSafetyUnavailableException Unavailable(string failure, Exception? exception)
    {
        _logger.LogError(exception, "Content safety screening failed ({Failure})", failure);
        return new ContentSafetyUnavailableException(
            $"Content could not be screened: the safety service is unavailable ({failure}).", exception);
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

        // Unreachable: the validator admits only the four categories, and every block is one that was requested.
        throw new InvalidOperationException($"Content safety reported a category it was not asked for: {category}.");
    }

    // Pieces of at most MaxChunkLength units, each starting ChunkOverlap units before the previous one ended, never
    // ending or starting between the halves of a surrogate pair.
    internal static IEnumerable<string> Chunks(string content)
    {
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
