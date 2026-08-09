using System.Text.RegularExpressions;
using Domain.AI.Iac;

namespace Infrastructure.AI.Iac;

/// <summary>
/// Parses tfsec's default text output into the scanner-neutral
/// <see cref="IacScanFinding"/> shape.
/// </summary>
/// <remarks>
/// tfsec's text output emits one block per result; each carries an ID, a severity,
/// and the offending resource, e.g.:
/// <code>
/// Result #1 CRITICAL Storage account uses an insecure TLS version
///   ID      azure-storage-use-secure-tls-policy
///   Severity CRITICAL
///   Resource azurerm_storage_account.primary
/// </code>
/// The parser is line-oriented: it keys off the <c>ID</c>, <c>Severity</c>, and
/// <c>Resource</c> lines and emits a finding when an ID and severity are both seen.
/// </remarks>
public static partial class TfsecParser
{
    private const string Scanner = "tfsec";

    [GeneratedRegex(@"^ID\s+(?<id>[A-Za-z0-9\-]+)", RegexOptions.Compiled)]
    private static partial Regex IdLine();

    [GeneratedRegex(@"^Severity\s+(?<sev>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SeverityLine();

    [GeneratedRegex(@"^Resource\s+(?<res>\S+)", RegexOptions.Compiled)]
    private static partial Regex ResourceLine();

    [GeneratedRegex(@"^Result\s+#\d+\s+\w+\s+(?<msg>.+)$", RegexOptions.Compiled)]
    private static partial Regex ResultLine();

    /// <summary>Parses tfsec text output into findings.</summary>
    /// <param name="output">The raw tfsec stdout.</param>
    /// <returns>One finding per result block carrying an ID + severity.</returns>
    public static IReadOnlyList<IacScanFinding> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var findings = new List<IacScanFinding>();
        string? id = null;
        string? message = null;
        string? resource = null;
        IacScanSeverity? severity = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            var result = ResultLine().Match(line);
            if (result.Success)
            {
                Flush(findings, id, message, resource, severity);
                id = null;
                resource = null;
                severity = null;
                message = result.Groups["msg"].Value.Trim();
                continue;
            }

            var idMatch = IdLine().Match(line);
            if (idMatch.Success)
            {
                id = idMatch.Groups["id"].Value;
                continue;
            }

            // A reported-but-unreadable severity resolves to Critical rather than leaving severity
            // null, which made Flush drop the finding outright — see
            // IacScanSeverityParser.ParseFindingSeverity.
            var sevMatch = SeverityLine().Match(line);
            if (sevMatch.Success)
            {
                severity = IacScanSeverityParser.ParseFindingSeverity(sevMatch.Groups["sev"].Value);
                continue;
            }

            var resMatch = ResourceLine().Match(line);
            if (resMatch.Success)
            {
                resource = resMatch.Groups["res"].Value;
            }
        }

        Flush(findings, id, message, resource, severity);
        return findings;
    }

    private static void Flush(
        List<IacScanFinding> findings,
        string? id,
        string? message,
        string? resource,
        IacScanSeverity? severity)
    {
        if (string.IsNullOrEmpty(id) || severity is null)
        {
            return;
        }

        findings.Add(new IacScanFinding
        {
            Scanner = Scanner,
            RuleId = id,
            Severity = severity.Value,
            Resource = resource ?? string.Empty,
            Message = message ?? string.Empty
        });
    }
}
