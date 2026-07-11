using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Services.Bundles;
using Application.AI.Common.Services.Governance;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.AI.Permissions;

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
/// When an envelope is active it emits two kinds of rule:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <strong>Bypass-immune Deny</strong> for every tool the bundle <em>declares</em> (drawn from the
///     ambient <see cref="EphemeralAgentOverlay"/>: the ephemeral agent's tool ceiling plus each owned
///     skill's tools) that the envelope does not grant. A catch-all <c>*</c> Deny cannot express
///     "deny all but the allowlist" because the resolver evaluates Deny (phase 1b) before Allow — a
///     wildcard Deny would also kill the granted tools — so the out-of-envelope set is enumerated and
///     denied by exact name. These denies are <see cref="ToolPermissionRule.IsBypassImmune"/> so no
///     auto-approve mode can lift them.
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
/// </list>
/// <para>
/// This is the strongest tier of a layered guarantee, not the whole of it: a tool the bundle did
/// <em>not</em> statically declare but tries to call at runtime is still blocked, because with no matching
/// Allow or baseline the resolver defaults to Ask and the fail-closed governor turns that into a denial.
/// Enumerating declared tools lets the common case be an explicit, bypass-immune Deny rather than a
/// default block.
/// </para>
/// </remarks>
public sealed class EnvelopePermissionRuleProvider : IPermissionRuleProvider
{
    /// <summary>Deny out-of-envelope tools ahead of any other rule.</summary>
    private const int DenyPriority = 1;

    /// <summary>Apply the autonomy-ceiling baseline after the deny set.</summary>
    private const int BaselinePriority = 5;

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

        // 1. Bypass-immune Deny for each declared tool the envelope does not grant. Build the grant set once
        //    so the membership test is O(1) per declared tool rather than a linear scan of the allowlist.
        var granted = new HashSet<string>(envelope.AllowedTools, StringComparer.OrdinalIgnoreCase);
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

        foreach (var toolName in envelope.AllowedTools)
        {
            rules.Add(new ToolPermissionRule(
                toolName,
                null,
                ceilingBehavior,
                PermissionRuleSource.CapabilityEnvelope,
                Priority: BaselinePriority,
                IsAuthoritativeBaseline: true));
        }

        return Task.FromResult<IReadOnlyList<ToolPermissionRule>>(rules);
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
