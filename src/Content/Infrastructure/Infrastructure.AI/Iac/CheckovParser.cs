using System.Text.RegularExpressions;
using Domain.AI.Iac;

namespace Infrastructure.AI.Iac;

/// <summary>
/// Parses Checkov's compact text output (<c>checkov -d . --compact</c>) into the
/// scanner-neutral <see cref="IacScanFinding"/> shape. Only failed checks become
/// findings — passed checks are not security issues.
/// </summary>
/// <remarks>
/// Checkov's compact output emits one block per check; a failed check looks like:
/// <code>
/// Check: CKV_AZURE_33: "Ensure Storage logging is enabled for Queue service"
///         FAILED for resource: azurerm_storage_account.primary
///         Severity: HIGH
/// </code>
/// The parser is line-oriented and tolerant: a check with no explicit severity
/// line is treated as <see cref="IacScanSeverity.Medium"/> (Checkov's default
/// weighting for an unscored policy).
/// </remarks>
public static partial class CheckovParser
{
    private const string Scanner = "checkov";

    [GeneratedRegex(@"^Check:\s+(?<rule>[A-Z0-9_]+):\s*""?(?<msg>[^""]*)""?", RegexOptions.Compiled)]
    private static partial Regex CheckLine();

    [GeneratedRegex(@"FAILED\s+for\s+resource:\s*(?<res>\S+)", RegexOptions.Compiled)]
    private static partial Regex FailedLine();

    [GeneratedRegex(@"Severity:\s*(?<sev>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SeverityLine();

    /// <summary>Parses Checkov compact output into failed-check findings.</summary>
    /// <param name="output">The raw Checkov stdout.</param>
    /// <returns>One finding per failed check.</returns>
    public static IReadOnlyList<IacScanFinding> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var findings = new List<IacScanFinding>();
        string? rule = null;
        string? message = null;
        string? resource = null;
        var failed = false;
        var severity = IacScanSeverity.Medium;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            var check = CheckLine().Match(line);
            if (check.Success)
            {
                Flush(findings, rule, message, resource, failed, severity);
                rule = check.Groups["rule"].Value;
                message = check.Groups["msg"].Value.Trim();
                resource = null;
                failed = false;
                severity = IacScanSeverity.Medium;
                continue;
            }

            var failedMatch = FailedLine().Match(line);
            if (failedMatch.Success)
            {
                failed = true;
                resource = failedMatch.Groups["res"].Value;
                continue;
            }

            // A reported-but-unreadable severity resolves to Critical rather than leaving the Medium
            // default in place — see IacScanSeverityParser.ParseFindingSeverity.
            var sevMatch = SeverityLine().Match(line);
            if (sevMatch.Success)
            {
                severity = IacScanSeverityParser.ParseFindingSeverity(sevMatch.Groups["sev"].Value);
            }
        }

        Flush(findings, rule, message, resource, failed, severity);
        return findings;
    }

    private static void Flush(
        List<IacScanFinding> findings,
        string? rule,
        string? message,
        string? resource,
        bool failed,
        IacScanSeverity severity)
    {
        if (!failed || string.IsNullOrEmpty(rule))
        {
            return;
        }

        findings.Add(new IacScanFinding
        {
            Scanner = Scanner,
            RuleId = rule,
            Severity = severity,
            Resource = resource ?? string.Empty,
            Message = message ?? string.Empty
        });
    }
}
