using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// Holds what each tool has declared about its own behaviour, so governance can decide from what a
/// tool <em>does</em> rather than from a list of names somebody has to keep correct forever.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a registry rather than a lookup.</strong> A first-party tool's behaviour can be read on
/// demand from its keyed-DI registration, but an external MCP tool's cannot: its declaration arrives
/// once, on the discovery call that publishes it to the model, and the object carrying it is not
/// reachable from a tool name at invocation time. Recording it at discovery is the only moment the
/// information exists.
/// </para>
/// <para>
/// <strong>Recording is not admission.</strong> Nothing here decides anything. It answers "what did
/// this tool say about itself, and who said it", and the governor decides what that is worth. Keeping
/// the two apart is what lets the decision be tested without a running MCP server.
/// </para>
/// </remarks>
public interface IToolBehaviorRegistry
{
    /// <summary>
    /// Records what an external MCP server advertised about one of its tools.
    /// </summary>
    /// <param name="toolName">The advertised tool name, as it will appear on a tool call.</param>
    /// <param name="behavior">The advertised behaviour, carrying the source's trust level.</param>
    /// <remarks>
    /// <para>
    /// Called on every discovery pass, not once at startup: a server may add tools while running, and
    /// re-reading the declaration each time is also what catches a server that changes what a tool
    /// claims after earning its place in the tool surface.
    /// </para>
    /// <para>
    /// <strong>When two servers advertise the same name, the declaration that does not exempt the tool
    /// wins</strong> — the same tightening-is-believed rule applied to collisions. A hostile server
    /// cannot loosen a tool by shadowing a name a stricter server already claimed. Detecting and
    /// reporting the collision itself is a separate concern.
    /// </para>
    /// </remarks>
    void RecordAdvertised(string toolName, ToolBehavior behavior);

    /// <summary>
    /// Returns the effective behaviour declared for a tool, or <see cref="ToolBehavior.Unknown"/> when
    /// nothing is known about it.
    /// </summary>
    /// <param name="toolName">The tool name being invoked.</param>
    /// <returns>
    /// The strictest declaration on record. When a name is both registered in this process and
    /// advertised externally, the declaration that does <em>not</em> exempt the tool from approval is
    /// the one returned.
    /// </returns>
    ToolBehavior Resolve(string toolName);
}
