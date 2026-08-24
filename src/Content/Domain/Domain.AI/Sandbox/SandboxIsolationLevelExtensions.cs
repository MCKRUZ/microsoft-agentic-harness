namespace Domain.AI.Sandbox;

/// <summary>
/// Helpers for raising a <see cref="SandboxIsolationLevel"/> to at least a given floor —
/// the "never downgrade" merge every isolation-elevation call site in the sandbox subsystem needs.
/// </summary>
/// <remarks>
/// Before #433, this shape — <c>(SandboxIsolationLevel)Math.Max((int)a, (int)b)</c> or an
/// equivalent hand-rolled comparison — was independently reimplemented at four call sites across
/// three files (<c>ToolUseStepExecutor.SyncProfileIsolation</c>, <c>McpConnectionManager</c>'s
/// inline profile rebind, and <c>ToolPermissionProfileResolver</c>'s two isolation-merge branches).
/// CLAUDE.md's own Common Mistakes section records this exact defect shape (a stale/un-synced
/// isolation tier) recurring twice before this consolidation.
/// </remarks>
public static class SandboxIsolationLevelExtensions
{
    /// <summary>
    /// Returns the stricter of this level and <paramref name="floor"/> — never a downgrade.
    /// </summary>
    /// <remarks>
    /// Relies on <see cref="SandboxIsolationLevel"/>'s declared numeric values being strictly
    /// ascending in isolation strength — see that enum's own remarks. Every isolation-elevation call
    /// site in the sandbox subsystem now funnels through this one method, so a future member added
    /// out of strictness order (e.g. a weaker tier given a higher numeric value than
    /// <see cref="SandboxIsolationLevel.Container"/>) would silently invert every floor merge at once.
    /// <c>SandboxIsolationLevelExtensionsTests</c> pins the current three-member ordering; a new
    /// member must extend that ordering, not merely satisfy it.
    /// </remarks>
    public static SandboxIsolationLevel AtLeast(this SandboxIsolationLevel level, SandboxIsolationLevel floor) =>
        (SandboxIsolationLevel)Math.Max((int)level, (int)floor);

    /// <summary>
    /// Returns <paramref name="profile"/> unchanged if its <see cref="ToolPermissionProfile.MinimumIsolation"/>
    /// already meets <paramref name="floor"/>; otherwise a copy with <c>MinimumIsolation</c> raised to
    /// <paramref name="floor"/>. Every other field is preserved as-is.
    /// </summary>
    /// <remarks>
    /// Callers that embed the result in a sandbox execution request should raise the floor before
    /// dispatch, not after: the attestation signed for every successful or failed run
    /// (<c>SandboxSessionAttestationSigner</c>, and <c>DockerSandboxExecutor</c>'s equivalent inline
    /// signing) reads <c>isolation</c> and <c>capabilitiesEnforcedBy</c> straight from the request's
    /// own <c>PermissionProfile.MinimumIsolation</c> — an un-elevated profile signs a stale, misleading
    /// isolation tier into the audit record even though the run itself executed at the elevated tier
    /// (#420). As of #434, a Docker outage is always a hard, attested refusal regardless of this
    /// field — neither <c>DockerSandboxExecutor</c> nor <c>DockerSandboxSessionFactory</c> branches
    /// on it anymore, since every first-party caller already reaches both exclusively through the
    /// <c>Container</c> keyed-DI slot.
    /// </remarks>
    public static ToolPermissionProfile WithMinimumIsolationAtLeast(
        this ToolPermissionProfile profile, SandboxIsolationLevel floor)
    {
        var raised = profile.MinimumIsolation.AtLeast(floor);
        return raised == profile.MinimumIsolation ? profile : profile with { MinimumIsolation = raised };
    }
}
