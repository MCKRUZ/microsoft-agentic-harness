using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Services.Bundles;
using Application.AI.Common.Services.Governance;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Microsoft.Extensions.Logging;

namespace Application.Core.Permissions;

/// <summary>
/// Emits permission rules from the ambient per-caller <see cref="CapabilityEnvelope"/> so a bundle run is
/// confined to exactly what the host granted it — the enforcement half of the capability envelope, carried
/// entirely by the existing 3-phase permission resolver with no new gate code.
/// </summary>
/// <remarks>
/// <para>
/// The provider contributes rules <em>only</em> while an envelope is ambient (i.e. inside a bundle run).
/// Off the bundle path <see cref="CapabilityEnvelopeAccessor.Current"/> is null and it returns nothing, so
/// every existing deployment is completely unaffected.
/// </para>
/// <para>
/// When an envelope is active it emits three kinds of rule:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <strong>Bypass-immune Deny</strong> for every tool the bundle <em>declares</em> (drawn from the
///     ambient <see cref="EphemeralAgentOverlay"/>: the ephemeral agent's tool ceiling plus each owned
///     skill's tools) that the envelope does not grant. These are ordinary phase-1b Deny rules marked
///     <see cref="ToolPermissionRule.IsBypassImmune"/>, so no auto-approve mode can lift them and the
///     denial is attributable to a named declaration rather than to the fallback.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>Autonomy-ceiling baseline</strong> for every granted tool: an
///     <see cref="ToolPermissionRule.IsAuthoritativeBaseline"/> rule whose behavior is the envelope's
///     <see cref="CapabilityEnvelope.AutonomyCeiling"/> mapped to Allow (Autonomous) or Ask (Supervised /
///     Restricted). Evaluated after Deny, so an out-of-envelope Deny still wins; and it only ever caps
///     autonomy — the governor's own graded-autonomy risk gate can tighten an Allow further but never
///     loosens it.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>Closing Deny</strong>: one catch-all <c>*</c> baseline Deny at the lowest possible
///     precedence, which is what makes the envelope an actual allowlist rather than a set of hints. It
///     is a <em>baseline</em>, not a plain Deny, for two reasons: baselines are resolved only in phase
///     1.5 (so this cannot pre-empt the granted tools in phase 1b), and they are arbitrated by pattern
///     specificity (so the per-name grants above, being exact, outrank it). Any name the envelope did
///     not grant reaches it and is denied inside phase 1.5 — before the resolver can fall through to a
///     host's generic autonomy-tier <c>*</c> Allow.
///     </description>
///   </item>
/// </list>
/// <para>
/// <strong>Why the closing Deny is load-bearing.</strong> Without it an ungranted name matched no
/// envelope rule at all, so resolution continued into the tier phase. A host configured with
/// <c>DefaultBehavior: Allow</c> and an <c>Autonomous</c> default tier — the shipped configuration of
/// both bundle-capable hosts — emits a <c>*</c> Allow there, and the ungranted tool resolved to
/// <em>Allow</em>. The envelope was documented as fail-closed while behaving fail-open for exactly the
/// names it never granted. The closing Deny is the mechanism that makes the documented invariant true;
/// removing it silently reopens the hole, which is why it is asserted directly in the test suite against
/// the real production provider set.
/// </para>
/// <para>
/// <strong>Grants are exact names, never patterns.</strong> Entries in
/// <see cref="CapabilityEnvelope.AllowedTools"/> are matched as literal tool names, matching
/// <see cref="CapabilityEnvelope.GrantsTool"/> and the exact-membership Deny half below. An entry
/// containing a wildcard is rejected with an error log and grants nothing: this is an authorization
/// allowlist, and an operator writing <c>"*"</c> to mean "all tools" would otherwise silently grant the
/// reserved plan capabilities (<c>llm_call</c>, <c>rag_retrieval</c>) that the envelope exists to gate,
/// while <c>GrantsTool</c> kept reporting them as ungranted. Widening a grant is not a failure mode an
/// allowlist may have, so the two halves are held to one meaning.
/// </para>
/// </remarks>
public sealed class EnvelopePermissionRuleProvider : IPermissionRuleProvider
{
    /// <summary>Deny out-of-envelope tools ahead of any other rule.</summary>
    private const int DenyPriority = 1;

    /// <summary>Apply the autonomy-ceiling baseline after the deny set.</summary>
    private const int BaselinePriority = 5;

    private readonly ILogger<EnvelopePermissionRuleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvelopePermissionRuleProvider"/> class.
    /// </summary>
    /// <param name="logger">Logger for rejected wildcard grants in an envelope's allowlist.</param>
    public EnvelopePermissionRuleProvider(ILogger<EnvelopePermissionRuleProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public PermissionRuleSource Source => PermissionRuleSource.CapabilityEnvelope;

    /// <inheritdoc />
    public Task<IReadOnlyList<ToolPermissionRule>> GetRulesAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var envelope = CapabilityEnvelopeAccessor.Current;
        if (envelope is null)
            return Task.FromResult<IReadOnlyList<ToolPermissionRule>>([]);

        var rules = new List<ToolPermissionRule>();
        var grantedNames = ValidGrants(envelope);

        // 1. Bypass-immune Deny for each declared tool the envelope does not grant. Build the grant set once
        //    so the membership test is O(1) per declared tool rather than a linear scan of the allowlist.
        var granted = new HashSet<string>(grantedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var toolName in EnumerateDeclaredTools(agentId))
        {
            if (!granted.Contains(toolName))
            {
                rules.Add(new ToolPermissionRule(
                    toolName,
                    null,
                    PermissionBehaviorType.Deny,
                    PermissionRuleSource.CapabilityEnvelope,
                    Priority: DenyPriority,
                    IsBypassImmune: true));
            }
        }

        // 2. Authoritative autonomy-ceiling baseline for each granted tool. Restricted and Supervised both
        //    map to Ask (approval required); only Autonomous maps to Allow (shared with every other rule
        //    provider so the tier-to-behavior policy cannot drift). NOTE: because live mid-tool-call approval
        //    routing is deferred, the governor currently treats Ask as a fail-closed block — so today a
        //    non-Autonomous ceiling effectively suspends the bundle's tool use rather than gating it for
        //    approval. This matches how plugin and tier baselines behave and is documented on
        //    CapabilityEnvelope.AutonomyCeiling; wiring the ceiling into live approval is a follow-up.
        var ceilingBehavior = envelope.AutonomyCeiling.ToDefaultPermissionBehavior();

        foreach (var toolName in grantedNames)
        {
            rules.Add(new ToolPermissionRule(
                toolName,
                null,
                ceilingBehavior,
                PermissionRuleSource.CapabilityEnvelope,
                Priority: BaselinePriority,
                IsAuthoritativeBaseline: true));
        }

        // 3. Closing Deny — everything the envelope did not grant. Emitted last and least specific so the
        //    per-name grants above outrank it, and at int.MaxValue priority so any future same-specificity
        //    baseline also wins. This is what turns the allowlist into a closed set: without it an ungranted
        //    name matches no envelope rule and resolution falls through to the host's generic autonomy tier,
        //    which in the shipped bundle-host configuration says Allow.
        rules.Add(new ToolPermissionRule(
            "*",
            null,
            PermissionBehaviorType.Deny,
            PermissionRuleSource.CapabilityEnvelope,
            Priority: int.MaxValue,
            IsAuthoritativeBaseline: true));

        return Task.FromResult<IReadOnlyList<ToolPermissionRule>>(rules);
    }

    /// <summary>
    /// The tool names an envelope actually grants: its <see cref="CapabilityEnvelope.AllowedTools"/>
    /// entries less any that contain a wildcard, which are rejected with an error log.
    /// </summary>
    /// <remarks>
    /// A wildcard entry is treated as a configuration error rather than a broad grant. The rest of the
    /// envelope — <see cref="CapabilityEnvelope.GrantsTool"/> and the exact-membership Deny half — reads
    /// these entries as literal names, so honouring a wildcard here would make one list mean two
    /// different things and would grant, without ever being written down, the reserved plan capabilities
    /// the envelope exists to gate. Dropping the entry fails closed: the run continues with the grants
    /// the operator did spell out.
    /// </remarks>
    private IReadOnlyList<string> ValidGrants(CapabilityEnvelope envelope)
    {
        var names = new List<string>(envelope.AllowedTools.Count);

        foreach (var entry in envelope.AllowedTools)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            if (entry.Contains('*', StringComparison.Ordinal))
            {
                _logger.LogError(
                    "Capability envelope: AllowedTools entry '{Entry}' contains a wildcard and was rejected. " +
                    "Envelope grants are exact tool names — list each tool the caller may invoke. A wildcard " +
                    "grant would also confer the reserved plan capabilities the envelope exists to gate.",
                    entry);
                continue;
            }

            names.Add(entry);
        }

        return names;
    }

    /// <summary>
    /// Collects the distinct tool names the bundle statically declares for <paramref name="agentId"/> —
    /// the ephemeral agent's own tool ceiling plus every tool named by its owned skills (their
    /// <c>AllowedTools</c> and <c>ToolDeclarations</c>). Read from the ambient overlay, and only when that
    /// overlay owns the agent being resolved; any other flow declares nothing here (its out-of-envelope
    /// calls are still caught by the fail-closed default).
    /// </summary>
    private static IReadOnlyCollection<string> EnumerateDeclaredTools(string agentId)
    {
        var overlay = EphemeralAgentOverlayAccessor.Current;
        if (overlay is null || !overlay.OwnsAgent(agentId))
            return [];

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var toolName in overlay.Agent.AllowedTools)
            if (!string.IsNullOrWhiteSpace(toolName))
                names.Add(toolName);

        foreach (var skill in overlay.OwnedSkills)
        {
            if (skill.AllowedTools is { Count: > 0 } allowed)
                foreach (var name in allowed)
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);

            if (skill.ToolDeclarations is { Count: > 0 } declarations)
                foreach (var declaration in declarations)
                    if (!string.IsNullOrWhiteSpace(declaration.Name))
                        names.Add(declaration.Name);
        }

        return names;
    }
}
