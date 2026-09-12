using Application.AI.Common.Services.Tools;
using Domain.AI.Bundles;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// The single source of truth for "does this capability envelope grant toolName", accounting for a
/// first-party tool's DI-registration-key vs. self-reported published-name divergence (#626) — shared
/// by every consumer that must agree on the answer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CapabilityEnvelope.GrantsTool"/> is a literal, case-insensitive membership test against
/// <see cref="CapabilityEnvelope.AllowedTools"/>. That is correct for an MCP tool grant (no
/// registration-key/published-name distinction exists for those) but incomplete for a first-party
/// tool: an operator can author a grant by the tool's DI registration key, while
/// <c>ThreePhasePermissionResolver.Matches</c> — and every other consumer that checks a tool's
/// identity at invocation — compares against the tool's self-reported <c>ITool.Name</c>, which can
/// legitimately disagree with its key.
/// </para>
/// <para>
/// <strong>Both halves of envelope enforcement must resolve this identically, by construction.</strong>
/// <c>EnvelopePermissionRuleProvider</c> builds permission rules from the envelope's grants,
/// and <c>ToolInvocationGovernor.EnvelopeGrantsToolWhenArmed</c> independently re-confirms a resolver
/// Allow against the same envelope as defence in depth — its own remarks state the two "must agree by
/// construction". Before this type existed, #626 gave the rule layer its own private published-name
/// expansion without updating the governor's check, so the rule layer would allow (or skip denying) a
/// key-authored grant, coverage the governor's raw, unexpanded check would still refuse — the fix was
/// runtime-inert for the case it was meant to fix, and correctness-review caught the mismatch this
/// method exists to close. Every consumer of "does the envelope grant this first-party tool" should go
/// through this type instead of calling <see cref="CapabilityEnvelope.GrantsTool"/> directly.
/// </para>
/// </remarks>
public sealed class CapabilityEnvelopeGrantResolver
{
    private readonly FirstPartyToolLookup _firstPartyToolLookup;
    private readonly ILogger<CapabilityEnvelopeGrantResolver> _logger;

    /// <summary>Initializes a new instance of the <see cref="CapabilityEnvelopeGrantResolver"/> class.</summary>
    /// <param name="firstPartyToolLookup">Resolves a first-party tool's self-reported published name.</param>
    /// <param name="logger">
    /// Logs a construction failure per <see cref="FirstPartyToolLookup.TryResolvePublishedName"/>'s
    /// documented calling contract — that method is deliberately pure, so every caller must supply its
    /// own one-line log-on-failure.
    /// </param>
    public CapabilityEnvelopeGrantResolver(
        FirstPartyToolLookup firstPartyToolLookup, ILogger<CapabilityEnvelopeGrantResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(firstPartyToolLookup);
        ArgumentNullException.ThrowIfNull(logger);
        _firstPartyToolLookup = firstPartyToolLookup;
        _logger = logger;
    }

    /// <summary>
    /// Wraps <see cref="FirstPartyToolLookup.TryResolvePublishedName"/> to honor its documented
    /// calling contract: log a construction failure (not merely "no such first-party tool", which is
    /// the normal, silent case for an MCP tool name).
    /// </summary>
    private bool TryResolvePublishedName(string toolKey, out string publishedName)
    {
        var resolved = _firstPartyToolLookup.TryResolvePublishedName(
            toolKey, out publishedName, out var constructionError);

        if (!resolved && constructionError is not null)
        {
            _logger.LogError(constructionError,
                "Could not construct first-party tool '{ToolKey}' to learn its published name for a " +
                "capability-envelope grant check — the raw grant entry still applies, but a caller " +
                "invoking it under a self-reported name that disagrees with the key would not be covered.",
                toolKey);
        }

        return resolved;
    }

    /// <summary>
    /// Whether <paramref name="envelope"/> grants <paramref name="toolName"/> — checking
    /// <paramref name="toolName"/>'s literal membership in <see cref="CapabilityEnvelope.AllowedTools"/>
    /// first, then (only when that fails) whether any grant entry is a first-party tool's DI key whose
    /// resolved published name matches <paramref name="toolName"/>.
    /// </summary>
    public bool Grants(CapabilityEnvelope envelope, string toolName)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        if (envelope.GrantsTool(toolName))
            return true;

        foreach (var grant in envelope.AllowedTools)
        {
            if (string.IsNullOrWhiteSpace(grant))
                continue;

            if (TryResolvePublishedName(grant, out var publishedName)
                && string.Equals(publishedName, toolName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Expands <paramref name="names"/> to also include each name's resolved, self-reported published
    /// name when it names a first-party tool by DI key and the two disagree — flattened and
    /// deduplicated case-insensitively, preserving first-seen order.
    /// </summary>
    public IReadOnlyList<string> ExpandWithPublishedNameCoverage(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var expanded = new List<string>(names.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (seen.Add(name))
                expanded.Add(name);

            if (TryResolvePublishedName(name, out var publishedName)
                && !string.Equals(publishedName, name, StringComparison.OrdinalIgnoreCase)
                && seen.Add(publishedName))
                expanded.Add(publishedName);
        }

        return expanded;
    }
}
