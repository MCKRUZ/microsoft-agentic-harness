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

    /// <summary>
    /// Whether this tool can be run through <c>POST /api/tools/{name}/invoke</c>.
    /// </summary>
    /// <remarks>
    /// <strong>False does not mean the caller lacks permission.</strong> It means the tool's result is
    /// not meaningful outside the process — the <c>render_*</c> family and <c>dashboard_control</c>
    /// return directives for a browser attached to a live agent run, and <c>delegate_task</c> expands
    /// one call into an open-ended sequence of agent turns. Such a tool is still listed because it
    /// remains fully usable from a workflow's <c>ToolUse</c> step, which is what a caller authoring a
    /// workflow needs to know. Invoking one directly answers <c>404</c>.
    /// </remarks>
    public required bool IsDirectlyInvocable { get; init; }
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

/// <summary>A request to run one operation of one tool.</summary>
/// <remarks>
/// <para>
/// <strong>One shape covers every tool.</strong> <c>ITool</c> exposes a single invocation signature —
/// an operation name and a parameter map — so there is no per-tool request type to bind against and no
/// contract that changes when the host registers a new tool. Which operations and parameters a given
/// tool accepts is discoverable from <c>GET /api/tools/{name}</c>.
/// </para>
/// <para>
/// <strong>The caller is not named here.</strong> Identity is taken from the bearer token at the
/// transport boundary, never from this body — a caller able to state their own identity could attribute
/// their tool use, in the host's governance audit, to somebody else.
/// </para>
/// </remarks>
public sealed record ToolInvocationRequest
{
    /// <summary>
    /// The operation to perform. Must be one the tool declares; anything else answers <c>400</c> with
    /// the accepted list.
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// The operation's arguments as a JSON object. Omit for operations that take none.
    /// </summary>
    /// <remarks>
    /// Values are converted to CLR types by the same code the agent path uses, so a tool sees identical
    /// arguments however it was reached. Nested objects and arrays reach the tool as their raw JSON
    /// text, because <c>ITool</c>'s parameter contract is flat.
    /// </remarks>
    public System.Text.Json.JsonElement? Parameters { get; init; }

    /// <summary>
    /// An optional shorter deadline, in seconds. Omit to use the host's configured ceiling.
    /// </summary>
    /// <remarks>
    /// A value above the ceiling is refused rather than clamped: a caller quietly given less time than
    /// they asked for would see a timeout with nothing in the response to explain it.
    /// </remarks>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>The result of a tool invocation.</summary>
/// <remarks>
/// A tool that ran and reported failure answers <c>200</c> with <see cref="Succeeded"/> false — the
/// invocation itself worked, and the HTTP status describes the invocation. Statuses in the 4xx/5xx
/// range mean the tool never executed.
/// </remarks>
public sealed record ToolInvocationResponse
{
    /// <summary>The tool this response came from.</summary>
    public required string Tool { get; init; }

    /// <summary>The operation that was run.</summary>
    public required string Operation { get; init; }

    /// <summary>Whether the tool reported success.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// The tool's output, sanitized. Null when the tool reported failure.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>The tool's failure message, sanitized. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Whether <see cref="Output"/> was cut short at the host's ceiling. A caller that cannot tell a
    /// complete result from a prefix will parse the prefix as complete.
    /// </summary>
    public required bool OutputTruncated { get; init; }

    /// <summary>How long the invocation took, in milliseconds.</summary>
    public required long DurationMs { get; init; }
}
