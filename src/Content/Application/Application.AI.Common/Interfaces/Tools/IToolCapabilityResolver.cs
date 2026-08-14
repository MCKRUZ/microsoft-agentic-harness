using Domain.AI.Governance;
using Domain.Common.Config.AI.Governance;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// Resolves what a tool can do with the data that flows through it — the classification the tool
/// composition check reasons over. A sibling to <see cref="IToolBehaviorRegistry"/>, answering a
/// different question: that registry describes a single tool's mutability, this describes the
/// direction data flows through it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Precedence, strict and in this order:</strong> a per-tool operator override
/// (authoritative, both directions) beats the base classification — a first-party tool's own
/// declaration, or, when that says nothing, the narrow built-in keyword heuristic against the
/// tool's published name — which is then augmented (never replaced) by an MCP open-world annotation
/// and a per-server operator override, both of which may only <em>add</em> capability bits, never
/// clear one another source already found. See <see cref="ToolCompositionGatingConfig"/>'s remarks for
/// why clearing is restricted to a named, per-tool override.
/// </para>
/// <para>
/// <strong>Unclassified is not a fail-closed default.</strong> A tool nothing can classify resolves to
/// <see cref="ToolCompositionCapability.None"/> — deliberately the opposite of how the rest of this harness treats
/// "unknown". See <see cref="ToolCapabilityProfile"/>'s remarks and <c>ToolCompositionAnalyzer</c> for
/// why: an unknown-means-both default would flag every agent holding two or more unclassified tools,
/// which is the failure this check exists to avoid.
/// </para>
/// </remarks>
public interface IToolCapabilityResolver
{
    /// <summary>
    /// Resolves the effective capability profile for a tool's published name — the name as it appears
    /// in an agent's assembled tool set, which for a bundle-owned MCP tool is the namespaced name, not
    /// the server's bare raw name.
    /// </summary>
    /// <param name="publishedToolName">The tool's published name.</param>
    /// <returns>
    /// The resolved profile. Never null; a tool nothing could classify returns
    /// <see cref="ToolCapabilityProfile.Unclassified"/> rather than throwing.
    /// </returns>
    ToolCapabilityProfile Resolve(string publishedToolName);
}
