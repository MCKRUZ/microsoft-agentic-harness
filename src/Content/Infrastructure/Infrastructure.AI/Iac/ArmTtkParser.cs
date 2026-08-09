using System.Text.RegularExpressions;
using Domain.AI.Iac;

namespace Infrastructure.AI.Iac;

/// <summary>
/// Parses ARM-TTK (Azure Resource Manager Template Toolkit) text output into the
/// scanner-neutral <see cref="IacScanFinding"/> shape. Only failed tests become
/// findings.
/// </summary>
/// <remarks>
/// ARM-TTK marks each test with a leading status glyph; a failure is rendered as
/// <c>[-]</c> and a pass as <c>[+]</c>:
/// <code>
/// [-] Secure-Params-No-Hardcoded-Default (12 ms)
///         Parameter 'adminPassword' must not have a hardcoded default. Severity: High
/// [+] DeploymentTemplate-Schema-Is-Correct (3 ms)
/// </code>
/// ARM-TTK does not assign a severity per test; the parser reads an explicit
/// <c>Severity:</c> token on the detail line when present and otherwise treats a
/// failed test as <see cref="IacScanSeverity.Medium"/>.
/// </remarks>
public static partial class ArmTtkParser
{
    private const string Scanner = "arm-ttk";

    [GeneratedRegex(@"^\[-\]\s+(?<rule>[^\(]+?)(?:\s+\(\d+\s*ms\))?$", RegexOptions.Compiled)]
    private static partial Regex FailedTestLine();

    [GeneratedRegex(@"Severity:\s*(?<sev>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SeverityToken();

    /// <summary>Parses ARM-TTK text output into failed-test findings.</summary>
    /// <param name="output">The raw ARM-TTK stdout.</param>
    /// <returns>One finding per failed test.</returns>
    public static IReadOnlyList<IacScanFinding> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var findings = new List<IacScanFinding>();
        string? rule = null;
        var detail = new List<string>();

        void Flush()
        {
            if (string.IsNullOrEmpty(rule))
            {
                return;
            }

            var joined = string.Join(" ", detail).Trim();
            var severity = IacScanSeverity.Medium;
            // A reported-but-unreadable severity resolves to Critical rather than leaving the Medium
            // default in place — see IacScanSeverityParser.ParseFindingSeverity.
            var sevMatch = SeverityToken().Match(joined);
            if (sevMatch.Success)
            {
                severity = IacScanSeverityParser.ParseFindingSeverity(sevMatch.Groups["sev"].Value);
            }

            findings.Add(new IacScanFinding
            {
                Scanner = Scanner,
                RuleId = rule.Trim(),
                Severity = severity,
                Resource = string.Empty,
                Message = joined
            });
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var failed = FailedTestLine().Match(line);
            if (failed.Success)
            {
                Flush();
                rule = failed.Groups["rule"].Value;
                detail = [];
                continue;
            }

            if (line.StartsWith("[+]", StringComparison.Ordinal))
            {
                Flush();
                rule = null;
                detail = [];
                continue;
            }

            if (rule is not null && line.Length > 0)
            {
                detail.Add(line);
            }
        }

        Flush();
        return findings;
    }
}
