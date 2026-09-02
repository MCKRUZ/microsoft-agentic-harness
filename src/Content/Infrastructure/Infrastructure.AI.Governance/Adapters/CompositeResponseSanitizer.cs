using System.Diagnostics;
using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>
/// Chains multiple <see cref="IResponseSanitizer"/> implementations in fixed order:
/// credentials first, then injection, then exfiltration.
/// Accumulates all findings and measures total sanitization duration.
/// </summary>
internal sealed class CompositeResponseSanitizer : ICompositeResponseSanitizer
{
    private readonly IResponseSanitizer[] _sanitizers;

    /// <summary>
    /// <strong>Deliberately a field default, never a constructor parameter — measured, not stylistic
    /// (#457's defect class).</strong> This type is a transitive dependency of
    /// <c>ContentFilterLocalLogRedactor</c>, which <c>IServiceCollectionExtensions.WrapWithLocalLogRedaction</c>
    /// resolves from INSIDE its own <see cref="ILoggerFactory"/> replacement factory delegate. Taking
    /// an <c>ILogger&lt;T&gt;</c> here therefore requires resolving <see cref="ILoggerFactory"/> while
    /// that same singleton is still being constructed: .NET's container cannot see a cycle hidden
    /// behind a factory delegate, so instead of throwing it re-enters the delegate forever and every
    /// host hangs on its first <c>ILogger&lt;T&gt;</c> resolution. Measured on the governance-enabled
    /// composition root: 20s+ no-completion with the constructor parameter, 236ms with this field —
    /// and <c>ValidateOnBuildSweepTests</c> passes either way, because <c>ValidateOnBuild</c> never
    /// executes a factory descriptor. <c>CompositeResponseSanitizerLoggerCycleTests</c> is the guard
    /// that does catch it; delete this field default in favour of a parameter and that test hangs.
    /// The practical consequence is that the timeout below is reported by
    /// <see cref="GovernanceMetrics.SanitizerTimeouts"/>, which needs no DI at all — the log line is
    /// kept only so a consumer debugging with a real logger attached has the sanitizer's name.
    /// </summary>
    private readonly ILogger<CompositeResponseSanitizer> _logger = NullLogger<CompositeResponseSanitizer>.Instance;

    public CompositeResponseSanitizer(IEnumerable<IResponseSanitizer> sanitizers)
    {
        _sanitizers = sanitizers
            .OrderBy(s => s.Category switch
            {
                SanitizationCategory.CredentialLeak => 0,
                SanitizationCategory.PromptInjection => 1,
                SanitizationCategory.ExfiltrationUrl => 2,
                _ => 3
            })
            .ToArray();
    }

    public SanitizationResult Sanitize(string content, string? toolName = null)
    {
        if (string.IsNullOrEmpty(content))
            return SanitizationResult.Clean(content ?? string.Empty);

        var sw = Stopwatch.StartNew();
        var originalContent = content;
        var currentContent = content;
        var allFindings = new List<SanitizationFinding>();

        foreach (var sanitizer in _sanitizers)
        {
            SanitizationResult result;
            try
            {
                result = sanitizer.Sanitize(currentContent, toolName);
            }
            catch (RegexMatchTimeoutException ex)
            {
                // Fails this ONE sanitizer's pass open, matching ScannerText.Matches' established
                // convention in the same governance layer — a hang in one rule must degrade one rule,
                // not the whole chain. Every [GeneratedRegex] in this chain now carries a finite
                // matchTimeoutMilliseconds (2000ms), so this is a defensive floor rather than an
                // expected occurrence: it also guards a consumer-replaced IResponseSanitizer, which
                // this interface is designed to allow, whose own patterns this codebase does not
                // control. currentContent is left as whatever the prior sanitizers in the chain already
                // produced — this sanitizer's own findings are simply absent, not fabricated.
                //
                // Counted, not only logged: a skipped pass emits no ResponseSanitizations of its own,
                // so on the metrics alone it is indistinguishable from content that was simply clean —
                // exactly the shape an attacker who can reliably time one rule out would produce. The
                // counter carries the same category/tool tag pair the sanitization counter does, so the
                // two line up on a dashboard.
                GovernanceMetrics.SanitizerTimeouts.Add(1,
                    new KeyValuePair<string, object?>(GovernanceConventions.SanitizationCategoryTag, sanitizer.Category.ToString()),
                    new KeyValuePair<string, object?>(GovernanceConventions.ToolName, toolName ?? "unknown"));
                _logger.LogWarning(ex,
                    "{Sanitizer} timed out sanitizing output of {ToolName}; its findings are skipped "
                    + "and the remaining sanitizers in the chain still run",
                    sanitizer.GetType().Name, toolName ?? "unknown");
                continue;
            }

            if (result.WasSanitized)
            {
                currentContent = result.SanitizedContent;
                allFindings.AddRange(result.Findings);

                foreach (var finding in result.Findings)
                {
                    GovernanceMetrics.ResponseSanitizations.Add(1,
                        new KeyValuePair<string, object?>(GovernanceConventions.SanitizationCategoryTag, finding.Category.ToString()),
                        new KeyValuePair<string, object?>(GovernanceConventions.ToolName, toolName ?? "unknown"));
                }
            }
        }

        sw.Stop();
        GovernanceMetrics.SanitizationDuration.Record(sw.Elapsed.TotalMilliseconds);

        if (allFindings.Count == 0)
            return SanitizationResult.Clean(originalContent);

        return SanitizationResult.WithFindings(currentContent, originalContent, allFindings.AsReadOnly());
    }
}
