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
    /// name when it names a first-party tool by DI key and the two disagree — keeping each tool's
    /// name-forms grouped, so a caller emitting one rule per form can record that those rules
    /// describe a single logical tool (#652). That pairing is knowable only here; recovering it later
    /// would mean resolving the tool again.
    /// </summary>
    /// <remarks>
    /// Deduplication is global across the whole input and case-insensitive: a form already
    /// contributed by an earlier name is dropped, and a name whose every form was already contributed
    /// yields no group at all. The flattened result is therefore exactly the name list the earlier
    /// flat <c>ExpandWithPublishedNameCoverage</c> produced — same order, same dedup — so rule
    /// coverage is unchanged by the grouping; that method was removed once its last caller moved
    /// here, rather than kept as an untested convenience.
    /// </remarks>
    public IReadOnlyList<ToolNameForms> ExpandToNameForms(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var groups = new List<ToolNameForms>(names.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var expanded = ExpandOne(name);

            var forms = new List<string>(expanded.Forms.Count);
            foreach (var form in expanded.Forms)
            {
                if (seen.Add(form))
                    forms.Add(form);
            }

            // Every form was already contributed by an earlier name: no rules to emit for it.
            if (forms.Count == 0)
                continue;

            groups.Add(expanded with { Forms = forms });
        }

        return groups;
    }

    /// <summary>
    /// The name-forms for a single name, with no cross-name deduplication — for a caller emitting
    /// rules one declared/denied tool at a time, where routing a one-element collection through
    /// <see cref="ExpandToNameForms"/> would allocate a list, a set and a result list per tool on a
    /// path that runs for every tool-permission resolution (/simplify finding), and would return a
    /// collection whose emptiness the caller then has to handle although it can never be empty.
    /// </summary>
    /// <param name="name">The declared, granted or denied name to expand.</param>
    public ToolNameForms ExpandOne(string name)
    {
        var diverges = TryResolvePublishedName(name, out var publishedName)
            && !string.Equals(publishedName, name, StringComparison.OrdinalIgnoreCase);

        // PublishedName is the name the agent actually invokes the tool by, which is what a summary
        // should show; when nothing diverges it is simply the name itself.
        return diverges
            ? new ToolNameForms(publishedName, [name, publishedName], Diverges: true)
            : new ToolNameForms(name, [name], Diverges: false);
    }
}

