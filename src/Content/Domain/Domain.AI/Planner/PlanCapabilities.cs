namespace Domain.AI.Planner;

/// <summary>
/// Well-known capability names a caller's <c>CapabilityEnvelope</c> can grant to plan-native
/// operations that do not flow through a named tool.
/// </summary>
/// <remarks>
/// <para>
/// The envelope model is tool-shaped: it grants by tool name (<c>AllowedTools</c>), MCP server name
/// (<c>AllowedMcpServers</c>), and autonomy ceiling. Plan <c>Retrieval</c> and <c>LlmCall</c> steps,
/// however, invoke the RAG orchestrators and the conversation pipeline directly — there is no tool
/// name on the wire for the envelope to match. These constants give such operations a stable,
/// documented name that is authorized through the <em>same</em> tool-invocation governor as any real
/// tool, so they inherit the full chain: envelope allowlist, autonomy ceiling, graded-autonomy risk
/// gate, declarative policy, audit, and denial-rate accounting.
/// </para>
/// <para>
/// <strong>Authorize, never match by hand.</strong> A step must never test
/// <c>AllowedTools.Contains(...)</c> for these names itself. Membership is only one of the inputs the
/// governor considers: the envelope's rule provider also emits an autonomy-ceiling baseline for every
/// granted name, so a name present in <c>AllowedTools</c> under a Restricted or Supervised ceiling
/// still resolves to Ask, which the fail-closed governor blocks. A hand-rolled membership check sees
/// the grant and misses the ceiling.
/// </para>
/// <para>
/// <strong>Risk classification.</strong> These names resolve no keyed <c>ITool</c>, so
/// <c>IToolRiskClassifier</c> returns its fail-safe default (Medium blast radius, treated as a state
/// change). That is deliberately conservative for what are read/inference operations: under graded
/// autonomy it can tighten a decision to Ask, never loosen one. They are intentionally not registered
/// as <c>ITool</c> implementations — that would publish them on the agent's callable tool surface,
/// which is a strictly larger change than giving the risk table a friendlier number.
/// </para>
/// <para>
/// <strong>These names share a namespace with real tool keys — treat them as reserved.</strong> They
/// are matched out of the same <c>AllowedTools</c> string space as keyed <c>ITool</c> registrations,
/// so a host that registers a tool under one of these keys silently merges the two grants: granting
/// the plan capability would also grant that tool, and vice versa. Nothing in the DI container
/// prevents it, because tool registration order is not guaranteed relative to the planner's. Hosts
/// registering tools should assert <see cref="IsReserved"/> is false for each key — the harness's own
/// registrations are covered by a test that does exactly that.
/// </para>
/// <para>
/// Withholding a name here is fail-closed for enveloped plan runs. With no ambient envelope (direct
/// in-process <c>IPlanExecutor</c> callers) the governor is a pass-through and behavior is unchanged.
/// </para>
/// </remarks>
public static class PlanCapabilities
{
    /// <summary>
    /// Grants plan <see cref="StepType.Retrieval"/> steps the right to query the RAG pipeline.
    /// A host that wants an enveloped plan run to perform retrieval must include this name in the
    /// envelope's <c>AllowedTools</c> <em>and</em> grant an autonomy ceiling that permits it;
    /// otherwise every Retrieval step in that run fails closed.
    /// </summary>
    public const string Retrieval = "rag_retrieval";

    /// <summary>
    /// Grants plan <see cref="StepType.LlmCall"/> steps the right to drive model inference on the
    /// host's credential with a plan-authored system prompt. Withholding it confines a caller to a
    /// plan that performs no inference at all — the control that stops an otherwise
    /// fully-constrained envelope from still buying unbounded tokens.
    /// </summary>
    public const string LlmCall = "llm_call";

    /// <summary>
    /// Every reserved plan-capability name. A tool must never be registered under one of these keys —
    /// see the collision note in the type remarks.
    /// </summary>
    public static IReadOnlyList<string> ReservedNames { get; } = [Retrieval, LlmCall];

    /// <summary>
    /// Whether <paramref name="name"/> is a reserved plan-capability name and therefore unavailable as
    /// a tool registration key. Case-insensitive, matching how the envelope matches allowlist entries.
    /// </summary>
    /// <param name="name">The candidate tool registration key.</param>
    /// <returns><c>true</c> when the name is reserved.</returns>
    public static bool IsReserved(string? name) =>
        name is not null && ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}
