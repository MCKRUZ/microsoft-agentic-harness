using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Governance;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="IToolCompositionAnalyzer"/>. Resolves every tool's capability profile, partitions
/// into source-capable and sink-capable tools, and emits one finding per co-resident (source tool,
/// sink tool, source bit, sink bit) fact.
/// </summary>
/// <remarks>
/// <strong>Findings are not filtered by posture.</strong> A finding here is a structural fact about
/// the tool set — which sources and sinks are co-resident — not a policy verdict. Whether a given
/// pairing is currently worth reporting or enforcing is decided separately and live, by
/// <see cref="ToolCompositionPostureResolver"/>, at the moment each of those questions is actually
/// asked. See <c>ToolCompositionFinding</c>'s remarks for why filtering here would silently break a
/// config change on an already-built agent.
/// </remarks>
public sealed class ToolCompositionAnalyzer : IToolCompositionAnalyzer
{
    /// <summary>
    /// Hard cap on emitted findings. A tool set producing more than this many distinct source/sink
    /// pairings is already a configuration worth a human's attention regardless of the exact count —
    /// see <see cref="ToolCompositionAssessment.Truncated"/>.
    /// </summary>
    private const int MaxFindings = 50;

    private readonly IToolCapabilityResolver _capabilityResolver;

    /// <summary>Initializes a new instance of the <see cref="ToolCompositionAnalyzer"/> class.</summary>
    public ToolCompositionAnalyzer(IToolCapabilityResolver capabilityResolver)
    {
        ArgumentNullException.ThrowIfNull(capabilityResolver);
        _capabilityResolver = capabilityResolver;
    }

    /// <inheritdoc />
    public ToolCompositionAssessment Analyze(IReadOnlyList<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        if (tools.Count == 0)
            return ToolCompositionAssessment.Empty;

        var profiles = new List<ToolCapabilityProfile>(tools.Count);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            // A tool set can legitimately contain the same name only once by construction upstream
            // (ToolChainBuilder dedups before this runs), but resolving defensively rather than
            // trusting that invariant costs nothing and avoids a duplicate profile if it ever does not.
            if (!seenNames.Add(tool.Name))
                continue;

            profiles.Add(_capabilityResolver.Resolve(tool.Name));
        }

        var unclassified = profiles
            .Where(p => p.Origin == ToolCapabilityOrigin.Unclassified)
            .Select(p => p.ToolName)
            .ToList();

        // Each entry names exactly one bit, so a source or sink tool carrying multiple bits appears
        // once per bit — the cross product below then evaluates each bit-pair independently against
        // its own configured posture.
        var sourceEntries = ExpandBySide(profiles, ToolCompositionCapabilities.SourceBits);
        var sinkEntries = ExpandBySide(profiles, ToolCompositionCapabilities.SinkBits);

        if (sourceEntries.Count == 0 || sinkEntries.Count == 0)
            return new ToolCompositionAssessment([], unclassified);

        // Materialized in full before capping — never short-circuited mid cross-product. A break at
        // exactly MaxFindings cannot tell "the cap coincided with the last real pairing" apart from
        // "more were dropped", and mislabeling the former as truncated is itself a false report on a
        // feature whose whole design goal is not reporting things that are not there. Tool sets are
        // small (a handful to a few dozen tools), so the full cross product is cheap regardless.
        var allFindings = new List<ToolCompositionFinding>();
        foreach (var source in sourceEntries)
        {
            foreach (var sink in sinkEntries)
            {
                // Self-pairs excluded: a tool that is both the source and the sink is one tool doing
                // one thing, which is #324's behaviour-gating job, not a composition risk. See
                // ToolCompositionCapability's remarks.
                if (string.Equals(source.Profile.ToolName, sink.Profile.ToolName, StringComparison.OrdinalIgnoreCase))
                    continue;

                allFindings.Add(new ToolCompositionFinding(
                    source.Profile.ToolName, source.Bit,
                    sink.Profile.ToolName, sink.Bit,
                    source.Profile.Origin, sink.Profile.Origin));
            }
        }

        var truncated = allFindings.Count > MaxFindings;
        var findings = truncated ? allFindings.GetRange(0, MaxFindings) : allFindings;

        return new ToolCompositionAssessment(findings, unclassified, truncated);
    }

    private static List<(ToolCapabilityProfile Profile, ToolCompositionCapability Bit)> ExpandBySide(
        List<ToolCapabilityProfile> profiles, IReadOnlyList<ToolCompositionCapability> bits)
    {
        var expanded = new List<(ToolCapabilityProfile, ToolCompositionCapability)>();
        foreach (var profile in profiles)
        {
            foreach (var bit in bits)
            {
                if ((profile.Capabilities & bit) == bit)
                    expanded.Add((profile, bit));
            }
        }
        return expanded;
    }
}
