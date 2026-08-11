using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Scans the aggregated MCP tool surface — every tool from every server, together — for threats that
/// only exist when tools are compared against each other or against their own past: tool name
/// collision, cross-server shadowing, and definition drift (rug pull). Complements
/// <see cref="IMcpSecurityScanner"/>, which inspects one tool definition in isolation and cannot see
/// any of these.
/// </summary>
public interface IMcpToolSurfaceScanner
{
    /// <summary>
    /// Scans the given tool surface and returns every structural finding. Read-only: the drift check
    /// compares each tool against its last-committed baseline but does not advance that baseline —
    /// call <see cref="CommitDefinitionPins"/> once the caller has decided which findings to withhold.
    /// </summary>
    /// <param name="tools">Every tool on the surface, attributed to the server that advertised it.</param>
    IReadOnlyList<McpSurfaceFinding> ScanSurface(IReadOnlyList<McpSurfaceTool> tools);

    /// <summary>
    /// Advances the drift baseline to each tool's current definition, except for
    /// <paramref name="excludeFromCommit"/>. Must be called once per build, with the same
    /// <paramref name="tools"/> passed to <see cref="ScanSurface"/>, after the withhold policy has
    /// decided which drift findings to honor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exclusion set is what makes a withheld rug-pull durable: skipping the commit for a
    /// withheld tool leaves its prior (accepted) baseline in place, so the next scan compares the
    /// attacker's definition against the last-known-good one again — not against itself — and keeps
    /// reporting drift. Committing unconditionally here is the defect this method exists to prevent:
    /// it would let a single scan both report a rug pull and silently accept it as the new normal in
    /// the same call, so the withhold would last exactly one build.
    /// </para>
    /// <para>
    /// <strong>Not atomic with <see cref="ScanSurface"/>, and not atomic with a concurrent commit.</strong>
    /// The withhold policy runs between the two calls, so two concurrent builds scanning the same tool
    /// can each read the same prior baseline before either commit lands. When both builds reach the
    /// <em>same</em> withhold decision — the common case, since the decision is a pure function of
    /// governance config and the finding's (fixed) severity — the only residual effect is a duplicate
    /// log line and metric increment for one definition transition. When the two builds reach
    /// <em>different</em> decisions — possible if governance config is reloaded between the two scans,
    /// or a compromised server answers the two concurrent requests inconsistently — <c>Set</c> is an
    /// unconditional overwrite, so whichever build's commit lands last wins outright, even if the other
    /// build correctly withheld the same tool. Closing this fully needs a compare-and-swap primitive
    /// (commit only if the store still holds the baseline this scan read); not built here because it
    /// requires both an unusual config-reload timing AND a server actively answering concurrent requests
    /// differently, and is a legitimate future increment rather than something this fix's scope covers.
    /// </para>
    /// </remarks>
    /// <param name="tools">The same tool surface passed to <see cref="ScanSurface"/> for this build.</param>
    /// <param name="excludeFromCommit">
    /// Tools whose drift finding was withheld this build — their baseline must not advance. Compared via
    /// <see cref="McpSurfaceToolReference.CaseInsensitiveComparer"/> by every implementation, matching
    /// the identity rule the rest of this subsystem already uses.
    /// </param>
    void CommitDefinitionPins(IReadOnlyList<McpSurfaceTool> tools, IReadOnlySet<McpSurfaceToolReference> excludeFromCommit);
}
