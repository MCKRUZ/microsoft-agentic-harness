using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Chains multiple <see cref="IResponseSanitizer"/> implementations in sequence,
/// accumulating findings and producing a merged <see cref="SanitizationResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>#580, documented rather than fixed: the same chain runs twice over one tool call, and its
/// metrics cannot currently tell the two runs apart.</strong>
/// <c>FileSystemToolResultStore.StoreIfLargeAsync</c> scans the full raw result at write time (up to
/// <c>ToolResultStorageConfig.MaxSpillChars</c>, several MB by default); <c>ToolCallAdmissionPipeline</c>
/// (via <c>ToolResultText.Sanitize</c>) scans again at the model-facing boundary, on the already-cut,
/// tens-of-kilobytes-scale text the model actually sees. Both calls emit the identical
/// <c>ResponseSanitizations</c>/<c>SanitizationDuration</c> tag set (<c>category</c> + <c>tool</c>), so
/// they are genuinely indistinguishable on the metric, and the duration histogram in particular mixes
/// two very different content-size populations into one distribution.
/// </para>
/// <para>
/// Accepted as intentional defense-in-depth for now — the write-time scan is what protects a fetched
/// page from a secret split across a page boundary (a page offset is caller-chosen, so redaction
/// cannot be deferred to read time; see that method's own remarks), and the model-facing scan is what
/// protects every OTHER exit path this pipeline has, including one that never touches the store at
/// all. This differs from <c>ToolOutputCompressionBehavior</c>'s documented double-redaction, which
/// pairs two DIFFERENT redactors each covering the other's gaps — here it is the SAME sanitizer run
/// twice, so that precedent does not fully transfer and is not cited as full justification on its own.
/// </para>
/// <para>
/// Not fixed here because <see cref="ICompositeResponseSanitizer"/> is a public, consumer-replaceable
/// interface — adding a scan-point parameter to <see cref="Sanitize"/> to disambiguate the metric
/// would be a breaking interface change for template consumers, disproportionate to a metrics-clarity
/// gap with no security or correctness impact of its own.
/// </para>
/// </remarks>
public interface ICompositeResponseSanitizer
{
    /// <summary>
    /// Runs all registered sanitizers in order against the content.
    /// </summary>
    /// <param name="content">The tool output to scan.</param>
    /// <param name="toolName">Optional tool name for context-aware scanning.</param>
    SanitizationResult Sanitize(string content, string? toolName = null);
}
