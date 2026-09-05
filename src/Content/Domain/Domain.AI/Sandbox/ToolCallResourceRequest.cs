namespace Domain.AI.Sandbox;

/// <summary>
/// The paths and hosts one tool call actually requested, extracted from its arguments before
/// <c>ICapabilityEnforcer.EnforceAsync</c> runs (#418).
/// </summary>
/// <remarks>
/// <strong>This type itself being <see langword="null"/> versus <see cref="Empty"/> is
/// load-bearing</strong> at every call site that carries it (<c>ToolCallAdmissionRequest</c>,
/// <c>IToolInvocationGovernor.AuthorizeAsync</c>, <c>ICapabilityEnforcer.EnforceAsync</c>):
/// <list type="bullet">
///   <item><description>
///     <see langword="null"/> means this call's resource usage could not be determined — the tool
///     declared resource parameters but the call's shape couldn't be read, or the call reached
///     enforcement through a path that never extracts one at all. When the tool's
///     <c>ToolPermissionProfile</c> has any path/deny scoping configured, this must refuse the
///     call rather than silently let it through — see <c>CapabilityEnforcer.EnforceAsync</c>'s
///     remarks for why treating "unknown" as "nothing" was the exact class of bug #405 shipped.
///   </description></item>
///   <item><description>
///     <see cref="Empty"/> means resource usage was genuinely determined and there is none — the
///     tool affirmatively declared this operation touches nothing scoped. This is the tool's own
///     trust boundary, the same model <c>ITool.RequiredCapabilities</c> already uses.
///   </description></item>
/// </list>
/// </remarks>
/// <param name="RequestedPaths">Filesystem paths this call named, in the order they appeared.</param>
/// <param name="RequestedHosts">Network hosts this call named, in the order they appeared.</param>
public sealed record ToolCallResourceRequest(
    IReadOnlyList<string> RequestedPaths,
    IReadOnlyList<string> RequestedHosts)
{
    /// <summary>Resource usage was determined, and this call touches no scoped path or host.</summary>
    public static readonly ToolCallResourceRequest Empty = new([], []);
}
