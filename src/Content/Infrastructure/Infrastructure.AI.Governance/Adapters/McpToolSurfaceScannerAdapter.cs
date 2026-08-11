using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Domain.Common.Config.AI;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>
/// Structural MCP tool-surface scanner: tool name collision, cross-server shadowing, and definition
/// drift (rug pull). Standalone implementation, modeled on AgentHound's structural detection rules —
/// see <see cref="McpSecurityScannerAdapter"/> for the per-tool content rules this complements.
/// </summary>
internal sealed class McpToolSurfaceScannerAdapter : IMcpToolSurfaceScanner
{
    private readonly IMcpDefinitionPinStore _pins;

    public McpToolSurfaceScannerAdapter(IMcpDefinitionPinStore pins)
    {
        _pins = pins;
    }

    /// <inheritdoc />
    public IReadOnlyList<McpSurfaceFinding> ScanSurface(IReadOnlyList<McpSurfaceTool> tools)
    {
        var findings = new List<McpSurfaceFinding>();

        ScanCollisions(tools, findings);
        ScanShadowing(tools, findings);
        ScanDrift(tools, findings);

        return findings;
    }

    /// <summary>
    /// Two servers advertising a tool with the same normalised name. Deliberately independent of
    /// which servers they are — a first-party tool is never passed into this scan colliding with an
    /// MCP tool, because the caller resolves that case (first-party always wins, silently, before the
    /// surface reaches here) as a routine reserved-name rule rather than a security finding. Anything
    /// that does reach here sharing a normalised name is, by construction, ambiguity between two
    /// sources neither of which the harness can vouch for over the other.
    /// </summary>
    private static void ScanCollisions(IReadOnlyList<McpSurfaceTool> tools, List<McpSurfaceFinding> findings)
    {
        var byNormalizedName = tools.GroupBy(t => Normalize(t.ToolName));

        foreach (var group in byNormalizedName)
        {
            var distinctServers = group.Select(t => t.ServerName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctServers.Count < 2)
                continue;

            findings.Add(new McpSurfaceFinding(
                McpThreatType.ToolNameCollision,
                ThreatLevel.Critical,
                $"{distinctServers.Count} servers advertise a tool named '{group.Key}'. Withholding all of them: " +
                "which one is legitimate cannot be determined from the definitions alone.",
                Confidence: 1.0,
                InvolvedTools: [.. group.Select(t => new McpSurfaceToolReference(t.ServerName, t.ToolName))]));
        }
    }

    /// <summary>
    /// A tool whose description names another server's tool, redirecting the agent's choice. A
    /// self-reference to a tool on the <em>same</em> server is not flagged — it is ordinary
    /// "use this tool for X, use the other one for Y" documentation within one server's own surface.
    /// </summary>
    private static void ScanShadowing(IReadOnlyList<McpSurfaceTool> tools, List<McpSurfaceFinding> findings)
    {
        // One whole-word pattern per distinct tool name, built once and reused across every candidate
        // description it's checked against — not once per (candidate, referenced) pair. This scan is
        // O(n²) in tool count by nature (every tool's description is checked against every other
        // tool's name), but the pattern only depends on referenced.ToolName, so without this cache the
        // same handful of patterns would be rebuilt up to n times each for no benefit.
        var wholeWordPatterns = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in tools)
        {
            foreach (var referenced in tools)
            {
                if (ReferenceEquals(candidate, referenced))
                    continue;

                if (string.Equals(candidate.ServerName, referenced.ServerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!MentionsToolName(candidate.Description, referenced.ToolName, wholeWordPatterns))
                    continue;

                findings.Add(new McpSurfaceFinding(
                    McpThreatType.ToolShadowing,
                    ThreatLevel.High,
                    $"Tool '{candidate.ToolName}' on server '{candidate.ServerName}' references tool " +
                    $"'{referenced.ToolName}' on a different server ('{referenced.ServerName}') by name.",
                    Confidence: 0.75,
                    InvolvedTools:
                    [
                        new McpSurfaceToolReference(candidate.ServerName, candidate.ToolName),
                        new McpSurfaceToolReference(referenced.ServerName, referenced.ToolName),
                    ]));
            }
        }
    }

    /// <summary>
    /// A tool whose description or schema hash differs from the last-recorded pin. Reports which
    /// surface changed, because the acceptance criteria for this feature call out the schema-only
    /// case specifically: a description that is byte-identical while a parameter description changes
    /// is exactly the attack a description-only pin would miss. Read-only — see
    /// <see cref="CommitDefinitionPins"/> for why the baseline must not advance here.
    /// </summary>
    private void ScanDrift(IReadOnlyList<McpSurfaceTool> tools, List<McpSurfaceFinding> findings)
    {
        foreach (var tool in tools)
        {
            // First-party tools are ours; there is no untrusted server to rug-pull them.
            if (tool.ServerName is null)
                continue;

            var descriptionHash = Hash(tool.Description);
            var schemaHash = Hash(tool.Schema ?? string.Empty);
            var previous = _pins.TryGet(tool.ServerName, tool.ToolName);

            if (previous is not null)
            {
                var descriptionChanged = !string.Equals(previous.DescriptionHash, descriptionHash, StringComparison.Ordinal);
                var schemaChanged = !string.Equals(previous.SchemaHash, schemaHash, StringComparison.Ordinal);

                if (descriptionChanged || schemaChanged)
                {
                    var changedSurface = (descriptionChanged, schemaChanged) switch
                    {
                        (true, true) => "description and schema",
                        (true, false) => "description",
                        _ => "schema",
                    };

                    findings.Add(new McpSurfaceFinding(
                        McpThreatType.RugPull,
                        ThreatLevel.High,
                        $"Tool '{tool.ToolName}' on server '{tool.ServerName}' changed its {changedSurface} " +
                        "since it was last seen.",
                        Confidence: 0.9,
                        InvolvedTools: [new McpSurfaceToolReference(tool.ServerName, tool.ToolName)]));
                }
            }
        }
    }

    /// <inheritdoc />
    public void CommitDefinitionPins(IReadOnlyList<McpSurfaceTool> tools, IReadOnlySet<McpSurfaceToolReference> excludeFromCommit)
    {
        foreach (var tool in tools)
        {
            // First-party tools never get a pin in the first place (see ScanDrift) — nothing to commit.
            if (tool.ServerName is null)
                continue;

            if (excludeFromCommit.Contains(new McpSurfaceToolReference(tool.ServerName, tool.ToolName)))
                continue;

            var pin = new McpToolDefinitionPin(Hash(tool.Description), Hash(tool.Schema ?? string.Empty));
            _pins.Set(tool.ServerName, tool.ToolName, pin);
        }
    }

    /// <summary>Trimmed, lower-cased — the same normalisation AgentHound's collision rule uses.</summary>
    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    /// <summary>
    /// Whether <paramref name="text"/> mentions <paramref name="toolName"/> as a whole word — checked
    /// against both the raw text and its <see cref="ScannerCanonicalizer"/>-folded shadow via
    /// <see cref="ScannerText"/>, the same evasion-resistant matching the per-tool content scanner
    /// (<see cref="McpSecurityScannerAdapter"/>) already applies to its own word-matching rules. A
    /// literal substring match on raw text alone would miss a description that names the target tool
    /// using a homoglyph, full-width character, or letter-spacing to render identically while defeating
    /// an ASCII comparison — the exact bypass <see cref="ScannerText"/> exists to close. A substring
    /// match with no boundary check would also false-positive on an unrelated tool name that happens to
    /// contain a shorter one (e.g. "search" inside "search_advanced"); this codebase's own naming
    /// convention is snake_case, so the boundary pattern treats underscore as a word character too.
    /// </summary>
    private static bool MentionsToolName(string text, string toolName, Dictionary<string, Regex> patternCache)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        if (!patternCache.TryGetValue(toolName, out var wholeWordPattern))
        {
            wholeWordPattern = new Regex(
                $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(toolName)}(?![\p{{L}}\p{{N}}_])",
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(1000));
            patternCache[toolName] = wholeWordPattern;
        }

        return ScannerText.For(text).Matches(wholeWordPattern);
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
