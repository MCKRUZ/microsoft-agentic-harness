using Domain.AI.Bundles;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// The catalog entry for a single tool: everything a caller needs to author a valid invocation
/// without holding a reference to the tool itself.
/// </summary>
/// <param name="Name">
/// The tool's name, which is also its keyed-DI registration key. This is the identifier a caller
/// passes back on an invocation, so it must round-trip: see <see cref="IToolCatalog"/> for why the
/// equality of the two is an invariant rather than a convention.
/// </param>
/// <param name="Description">The tool's own description — the same text the LLM is shown.</param>
/// <param name="SupportedOperations">
/// The operations the tool accepts. An invocation naming anything outside this list is rejected by
/// the tool itself; publishing the list lets a caller find that out before spending a request.
/// </param>
/// <param name="Risk">
/// The tool's blast radius and read-only flag, reusing the same <see cref="ToolRiskProfile"/> the
/// graded-autonomy evaluator reasons about. Advertising a different risk shape here than the one
/// governance enforces would be a lie the caller could act on.
/// </param>
/// <param name="IsConcurrencySafe">
/// Whether the tool may run alongside other invocations. Callers batching work need this; it is
/// fail-closed (false) for any tool that does not declare otherwise.
/// </param>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    IReadOnlyList<string> SupportedOperations,
    ToolRiskProfile Risk,
    bool IsConcurrencySafe);

/// <summary>
/// Enumerates the tools registered in this host, filtered to those a caller's capability envelope
/// actually grants.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is deliberately no "list everything" method.</strong> Every member takes a
/// <see cref="CapabilityEnvelope"/>, so a caller-facing surface cannot advertise a tool the caller
/// could not invoke, and no future caller can reach an unfiltered listing by accident. An
/// unfiltered catalog would be a reconnaissance surface: it discloses the host's whole tool
/// inventory — including tools reachable only to higher-privileged callers — to anyone who can
/// authenticate.
/// </para>
/// <para>
/// <strong>The name/key invariant.</strong> A tool's <see cref="ITool.Name"/> and its keyed-DI
/// registration key are assumed equal throughout the harness: <c>ToolChainBuilder</c> and
/// <c>ToolRiskClassifier</c> both resolve by name via <c>GetKeyedService&lt;ITool&gt;(name)</c>,
/// and the envelope's <see cref="CapabilityEnvelope.AllowedTools"/> is a list of names. A tool
/// registered under a key that differs from its own <c>Name</c> would be advertised by this
/// catalog under a name that nothing can resolve, and would be filtered against the wrong grant.
/// The invariant is asserted against the real container by
/// <c>ToolsControllerIntegrationTests.EveryCatalogedName_ResolvesToAToolThatAgreesWithIt</c>
/// rather than left to convention.
/// </para>
/// </remarks>
public interface IToolCatalog
{
    /// <summary>
    /// Lists the tools <paramref name="envelope"/> grants, ordered by name so the listing is stable
    /// across calls and hosts.
    /// </summary>
    /// <param name="envelope">The caller's resolved envelope. An envelope granting nothing yields an empty list.</param>
    /// <returns>The granted tools; never null.</returns>
    IReadOnlyList<ToolDescriptor> ListGranted(CapabilityEnvelope envelope);

    /// <summary>
    /// Finds a single granted tool by name, matched case-insensitively to mirror how the permission
    /// resolver and <see cref="CapabilityEnvelope.GrantsTool"/> match.
    /// </summary>
    /// <param name="toolName">The tool name to look up.</param>
    /// <param name="envelope">The caller's resolved envelope.</param>
    /// <returns>
    /// The descriptor, or <see langword="null"/> when the tool does not exist <em>or</em> exists but is
    /// not granted. The two cases are deliberately indistinguishable — telling an ungranted caller that
    /// a tool exists is the disclosure this interface's filtering exists to prevent.
    /// </returns>
    ToolDescriptor? FindGranted(string toolName, CapabilityEnvelope envelope);
}
