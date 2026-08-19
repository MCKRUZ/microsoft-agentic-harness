using Domain.Common.Config.AI;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// The one correct way to write a governance decision to <see cref="IGovernanceAuditService"/>: gate
/// on the operator's <see cref="GovernanceConfig.EnableAudit"/> toggle, default a missing agent id to
/// <c>"unknown"</c>, then write.
/// </summary>
/// <remarks>
/// This exact shape — <c>if (config.EnableAudit) auditService.Log(agentId ?? "unknown", action,
/// decision)</c> — was independently reimplemented at nine call sites across seven files before this
/// extraction (#430), with no two quite alike: some read <c>GovernanceConfig</c> from a captured
/// <see cref="IOptionsMonitor{TOptions}"/> field, one received the snapshot as a method parameter, and
/// one (<c>ToolPermissionProfileResolver.LogRefusal</c>) treated <em>both</em> the service and the
/// config as optional, defaulting an absent config to audit-on. That last variant is not a bug to
/// normalize away — <c>ToolPermissionProfileResolver</c> is constructed in test and partially-composed
/// hosts without a governance config at all, and defaulting to "audit if you can" is the safer
/// direction to fail in. Every overload below preserves it.
/// </remarks>
public static class GovernanceAuditGateExtensions
{
    /// <summary>
    /// Writes <paramref name="decision"/> to <paramref name="auditService"/> when
    /// <paramref name="governanceConfig"/>'s current <see cref="GovernanceConfig.EnableAudit"/> is
    /// <see langword="true"/> — the common case, where a caller holds a live
    /// <see cref="IOptionsMonitor{TOptions}"/> rather than a single snapshot.
    /// </summary>
    /// <param name="auditService">
    /// May be <see langword="null"/> in a host that has not registered a governance audit sink; the
    /// write is then a no-op rather than a null-reference failure.
    /// </param>
    /// <param name="governanceConfig">
    /// May be <see langword="null"/> in a partially-composed host (e.g. a unit test); a missing config
    /// is treated as audit-on, matching <c>ToolPermissionProfileResolver.LogRefusal</c>'s original
    /// semantic — an operator who never configured governance has not asked for audit logging to be
    /// silently disabled.
    /// </param>
    /// <param name="agentId">The acting agent's id, or <see langword="null"/> if unknown to the caller.</param>
    /// <param name="action">The action that was evaluated (e.g. a tool name).</param>
    /// <param name="decision">The governance decision (allow, deny, warn, etc.).</param>
    public static void LogIfAuditEnabled(
        this IGovernanceAuditService? auditService,
        IOptionsMonitor<GovernanceConfig>? governanceConfig,
        string? agentId,
        string action,
        string decision) =>
        auditService.LogIfAuditEnabled(governanceConfig?.CurrentValue, agentId, action, decision);

    /// <summary>
    /// As <see cref="LogIfAuditEnabled(IGovernanceAuditService?, IOptionsMonitor{GovernanceConfig}?, string?, string, string)"/>,
    /// but for a caller that already holds a resolved <see cref="GovernanceConfig"/> snapshot rather
    /// than the live <see cref="IOptionsMonitor{TOptions}"/> — e.g. one that received the snapshot as
    /// its own method parameter and would otherwise have to thread the monitor through separately just
    /// to reach this gate.
    /// </summary>
    public static void LogIfAuditEnabled(
        this IGovernanceAuditService? auditService,
        GovernanceConfig? governanceConfig,
        string? agentId,
        string action,
        string decision)
    {
        if (!ShouldLog(auditService, governanceConfig))
            return;

        auditService!.Log(agentId ?? "unknown", action, decision);
    }

    /// <summary>
    /// As <see cref="LogIfAuditEnabled(IGovernanceAuditService?, GovernanceConfig?, string?, string, string)"/>,
    /// but for a caller whose <paramref name="decision"/> string is non-trivial to build (e.g. a LINQ
    /// projection over a findings collection) — deferred so the cost is paid only when the gate actually
    /// passes, not on every call regardless of the operator's audit setting. No monitor-based overload
    /// of this one exists: at the time of writing, every lazy-decision caller already holds a resolved
    /// <see cref="GovernanceConfig"/> snapshot rather than the live monitor, so a delegating overload
    /// would have exactly one caller — add one if and when a second monitor-holding caller needs it.
    /// </summary>
    public static void LogIfAuditEnabled(
        this IGovernanceAuditService? auditService,
        GovernanceConfig? governanceConfig,
        string? agentId,
        string action,
        Func<string> decision)
    {
        if (!ShouldLog(auditService, governanceConfig))
            return;

        auditService!.Log(agentId ?? "unknown", action, decision());
    }

    /// <summary>
    /// The shared gate check both snapshot-based overloads apply before writing: a missing
    /// <paramref name="auditService"/> means there is nothing to write to, and a present
    /// <paramref name="governanceConfig"/> with <see cref="GovernanceConfig.EnableAudit"/> off means the
    /// operator has explicitly opted out. Kept out of the two overloads' own bodies — rather than each
    /// re-checking inline — specifically so the lazy overload can decide whether to log <em>before</em>
    /// invoking <paramref name="governanceConfig"/>'s decision factory, not after.
    /// </summary>
    private static bool ShouldLog(IGovernanceAuditService? auditService, GovernanceConfig? governanceConfig) =>
        auditService is not null && (governanceConfig is null || governanceConfig.EnableAudit);
}
