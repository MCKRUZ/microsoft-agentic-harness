using Domain.AI.Changes;

namespace Presentation.ExecutionApi.DTOs;

/// <summary>The caller-visible view of one tool it may invoke.</summary>
/// <remarks>
/// A projection of <c>ToolDescriptor</c> rather than the descriptor itself. The wire shape is a
/// published contract external automation binds to, so it is stated here explicitly instead of
/// inheriting whatever fields an internal type happens to grow — the same reason
/// <see cref="WorkflowRunResponse"/> projects rather than returning the stored run record.
/// </remarks>
public sealed record ToolCatalogEntry
{
    /// <summary>The tool's name — the identifier used to invoke it.</summary>
    public required string Name { get; init; }

    /// <summary>What the tool does. This is the same description the model is shown.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The operations this tool accepts. An invocation naming anything else is rejected by the tool.
    /// </summary>
    public required IReadOnlyList<string> Operations { get; init; }

    /// <summary>
    /// The tool's blast radius — how much a single call can do. Callers should treat this as the
    /// harness does: it is a ceiling on what the host will auto-approve, not a promise about
    /// whether any particular invocation succeeds.
    /// </summary>
    /// <remarks>
    /// Travels as a <em>name</em> (<c>"Trivial"</c> … <c>"Critical"</c>), not an ordinal — the host
    /// registers a <c>JsonStringEnumConverter</c>. That is the durable form for an external contract:
    /// an ordinal would silently change meaning if a value were ever inserted into the enum.
    /// </remarks>
    public required BlastRadius RiskTier { get; init; }

    /// <summary>Whether the tool only reads state. Fail-closed: false unless the tool declares otherwise.</summary>
    public required bool IsReadOnly { get; init; }

    /// <summary>
    /// Whether the tool is safe to invoke alongside other calls. Fail-closed: false unless the tool
    /// declares otherwise.
    /// </summary>
    public required bool IsConcurrencySafe { get; init; }
}

/// <summary>The tools the calling credential may invoke in this host.</summary>
/// <remarks>
/// <para>
/// <strong>This is not the host's tool inventory.</strong> It is the intersection of what the host
/// registers and what the caller's capability envelope grants, so two credentials will legitimately
/// see different listings and neither sees the whole. An empty list means the envelope grants no
/// tools — which is the shipped default — not that the host has none.
/// </para>
/// </remarks>
public sealed record ToolCatalogResponse
{
    /// <summary>The granted tools, ordered by name.</summary>
    public required IReadOnlyList<ToolCatalogEntry> Tools { get; init; }
}
