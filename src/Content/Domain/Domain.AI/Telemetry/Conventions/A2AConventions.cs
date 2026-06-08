namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// OpenTelemetry semantic-convention attribute, span, and event names for the
/// harness Agent-to-Agent (A2A) surface (PR-7).
/// </summary>
/// <remarks>
/// <para>
/// The OTel GenAI semconv (Experimental as of 2026-06) does not yet cover the
/// A2A protocol. Harness extensions therefore live under
/// <c>gen_ai.a2a.*</c> — outside the official reserved namespace — and re-export
/// the spec-covered concepts (system, operation, conversation) from
/// <see cref="GenAiSemconvRegistry"/>.
/// </para>
/// <para>
/// Span tree:
/// <list type="bullet">
/// <item><description><c>a2a.client {callee_id}</c> — emitted by <c>IA2AClient.CallAsync</c> on the caller side, kind <c>Client</c>.</description></item>
/// <item><description><c>a2a.server {callee_skill}</c> — emitted by <c>IA2AServer</c> dispatch on the callee side, kind <c>Server</c>.</description></item>
/// </list>
/// The two spans share <see cref="CorrelationId"/>; the callee span links to the
/// caller span through standard W3C trace-context propagation, AND through the
/// correlation id attribute so cross-process logs join even when the trace
/// context propagator is misconfigured.
/// </para>
/// </remarks>
public static class A2AConventions
{
    // ─────────────────────────────────────────────────────────────────────
    // ActivitySource
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Name of the OTel <see cref="System.Diagnostics.ActivitySource"/> used by
    /// the A2A client and server emitters. Single source per process; the
    /// Presentation layer registers this name with the OTel tracer provider.
    /// </summary>
    public const string ActivitySourceName = "AgenticHarness.A2A";

    // ─────────────────────────────────────────────────────────────────────
    // Span names
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Span name prefix for the caller-side span. Full name: <c>a2a.client {callee_agent_id}</c>.</summary>
    public const string SpanNameClientPrefix = "a2a.client ";

    /// <summary>Span name prefix for the callee-side span. Full name: <c>a2a.server {callee_skill}</c>.</summary>
    public const string SpanNameServerPrefix = "a2a.server ";

    // ─────────────────────────────────────────────────────────────────────
    // Operation
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Operation value used on both client and server A2A spans. Harness-vendored
    /// because the OTel GenAI <c>gen_ai.operation.name</c> enum does not yet
    /// include an A2A operation.
    /// </summary>
    public const string OperationInvokeA2A = "invoke_a2a";

    // ─────────────────────────────────────────────────────────────────────
    // Caller / callee identity (harness-vendored)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Caller agent identifier on both spans. Sourced from
    /// <c>IAgentExecutionContext.AgentIdentity.Id</c> at the call site.
    /// </summary>
    public const string CallerId = "gen_ai.a2a.caller.id";

    /// <summary>
    /// Caller agent identity kind on both spans. Sourced from
    /// <c>IAgentExecutionContext.AgentIdentity.Kind</c>; one of the
    /// <c>AgentIdentityKind</c> string values.
    /// </summary>
    public const string CallerKind = "gen_ai.a2a.caller.kind";

    /// <summary>
    /// Callee agent identifier on both spans. Resolved by the client before the
    /// call and confirmed by the server on dispatch.
    /// </summary>
    public const string CalleeId = "gen_ai.a2a.callee.id";

    /// <summary>
    /// Skill name the call is targeting on the callee side. Optional — null for
    /// calls that target the callee's default skill.
    /// </summary>
    public const string CalleeSkill = "gen_ai.a2a.callee.skill";

    // ─────────────────────────────────────────────────────────────────────
    // Correlation
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Correlation id shared by the caller and callee spans. Stamped by the
    /// client (UUIDv4) and echoed by the server. Lets log search join the two
    /// spans even when the W3C trace-context propagator is broken.
    /// </summary>
    public const string CorrelationId = "gen_ai.a2a.correlation_id";

    // ─────────────────────────────────────────────────────────────────────
    // Transport
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Transport used for the call: <see cref="TransportInProcess"/> or
    /// <see cref="TransportHttp"/>. Distinguishes a same-process A2A hop from
    /// a cross-process hop so dashboards can filter cleanly.
    /// </summary>
    public const string Transport = "gen_ai.a2a.transport";

    /// <summary>Transport value: same-process direct dispatch.</summary>
    public const string TransportInProcess = "in_process";

    /// <summary>Transport value: HTTP over (m)TLS between processes.</summary>
    public const string TransportHttp = "http";

    // ─────────────────────────────────────────────────────────────────────
    // Auth
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Authentication scheme used for the call: <see cref="AuthSchemeInProcess"/>,
    /// <see cref="AuthSchemeMtlsJwt"/>, or a future scheme name. Allows dashboards
    /// to alert on calls that fell back to a weaker scheme.
    /// </summary>
    public const string AuthScheme = "gen_ai.a2a.auth.scheme";

    /// <summary>Auth-scheme value: ambient identity context trust (in-process only).</summary>
    public const string AuthSchemeInProcess = "ambient";

    /// <summary>Auth-scheme value: mutual TLS plus workload-identity JWT bearer.</summary>
    public const string AuthSchemeMtlsJwt = "mtls+jwt";

    // ─────────────────────────────────────────────────────────────────────
    // Error
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Error code on a failed call: a stable <c>a2a.*</c> identifier such as
    /// <c>a2a.auth_rejected</c> or <c>a2a.timeout</c>. Server populates this on
    /// failures; client mirrors the server-supplied code when present.
    /// </summary>
    public const string ErrorCode = "gen_ai.a2a.error.code";

    // ─────────────────────────────────────────────────────────────────────
    // Envelope versioning
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Envelope schema version on both spans. Currently <c>1</c>; bumping the
    /// envelope shape requires bumping this constant and the version-pin test.
    /// </summary>
    public const string EnvelopeVersion = "gen_ai.a2a.envelope.version";
}
