using System.Diagnostics;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>
/// Chains multiple <see cref="IResponseSanitizer"/> implementations in fixed order:
/// credentials first, then injection, then exfiltration.
/// Accumulates all findings and measures total sanitization duration.
/// </summary>
internal sealed class CompositeResponseSanitizer : ICompositeResponseSanitizer
{
    private readonly IResponseSanitizer[] _sanitizers;

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
            var result = sanitizer.Sanitize(currentContent, toolName);
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
