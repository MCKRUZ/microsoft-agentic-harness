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
    public static SandboxIsolationLevel AtLeast(this SandboxIsolationLevel level, SandboxIsolationLevel floor) =>
        (SandboxIsolationLevel)Math.Max((int)level, (int)floor);

    /// <summary>
    /// Returns <paramref name="profile"/> unchanged if its <see cref="ToolPermissionProfile.MinimumIsolation"/>
    /// already meets <paramref name="floor"/>; otherwise a copy with <c>MinimumIsolation</c> raised to
    /// <paramref name="floor"/>. Every other field is preserved as-is.
    /// </summary>
    /// <remarks>
    /// Callers that embed the result in a sandbox execution request should raise the floor before
    /// dispatch, not after: <c>DockerSandboxExecutor.HandleDockerUnavailableAsync</c> (Infrastructure
    /// layer) reads a request's own <c>PermissionProfile.MinimumIsolation</c> to decide whether a
    /// Docker outage is a hard, attested refusal or a soft, unattested fallback — an un-elevated
    /// profile can misroute an autonomy-elevated call into the unattested branch (#420).
    /// </remarks>
    public static ToolPermissionProfile WithMinimumIsolationAtLeast(
        this ToolPermissionProfile profile, SandboxIsolationLevel floor)
    {
        var raised = profile.MinimumIsolation.AtLeast(floor);
        return raised == profile.MinimumIsolation ? profile : profile with { MinimumIsolation = raised };
    }
}
