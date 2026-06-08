using System.Text.Json.Serialization;

namespace Domain.AI.A2A;

/// <summary>
/// Wire-shape envelope carrying identity and correlation metadata for every
/// A2A call. Used by both <c>IA2AClient</c> (stamps headers) and <c>IA2AServer</c>
/// (reads + re-establishes context) so an in-process call and a cross-process
/// call speak the same shape.
/// </summary>
/// <remarks>
/// <para>
/// All property names are explicitly camelCase via <see cref="JsonPropertyNameAttribute"/>
/// — never rely on serializer naming-policy defaults, since the envelope crosses
/// process boundaries and a serializer config drift on either side would silently
/// drop fields. This is also why the type is a sealed record with init-only
/// properties: post-deserialization mutation is a bug source for distributed
/// protocols.
/// </para>
/// <para>
/// <see cref="SchemaVersion"/> is fixed at <see cref="CurrentSchemaVersion"/> for
/// PR-7. Any breaking shape change requires bumping the constant AND updating
/// the version-pin test in <c>Infrastructure.AI.Tests/A2A</c>.
/// </para>
/// </remarks>
public sealed record A2AEnvelope
{
    /// <summary>
    /// Current envelope schema version. Increments on any breaking change to
    /// the JSON shape (added optional fields do not bump).
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Envelope schema version stamped on each message. Required so a callee
    /// running an older harness can reject an envelope it cannot interpret.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// Correlation id shared by the caller and callee for this single A2A hop.
    /// Stamped by the client (UUIDv4) and echoed unchanged by the server.
    /// Surfaces as <c>gen_ai.a2a.correlation_id</c> on both spans.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Caller agent identifier (the agent making the call). Sourced from
    /// <c>IAgentExecutionContext.AgentIdentity.Id</c> at the call site. Required
    /// on every envelope so the callee can attribute the work even when the
    /// transport-level identity (cert SAN / JWT sub) is missing or generic.
    /// </summary>
    [JsonPropertyName("callerAgentId")]
    public required string CallerAgentId { get; init; }

    /// <summary>
    /// Caller identity kind, as the string value of <c>AgentIdentityKind</c>.
    /// Cross-process callees use this to decide whether to honor the caller's
    /// declared identity or fall back to the JWT-asserted one.
    /// </summary>
    [JsonPropertyName("callerKind")]
    public required string CallerKind { get; init; }

    /// <summary>
    /// Callee agent identifier (the agent being called). Stamped by the client
    /// from its routing decision. Surfaces as <c>gen_ai.a2a.callee.id</c>.
    /// </summary>
    [JsonPropertyName("calleeAgentId")]
    public required string CalleeAgentId { get; init; }

    /// <summary>
    /// Optional skill name on the callee side. Null when the call targets the
    /// callee's default skill. Servers must treat null as "default skill".
    /// </summary>
    [JsonPropertyName("calleeSkill")]
    public string? CalleeSkill { get; init; }

    /// <summary>
    /// W3C trace-context <c>traceparent</c> header value captured on the caller
    /// side. Null for in-process calls (the local <c>Activity</c> parent links
    /// the spans directly); set for cross-process calls so the callee can
    /// extract the upstream context onto its server span.
    /// </summary>
    [JsonPropertyName("traceparent")]
    public string? Traceparent { get; init; }

    /// <summary>
    /// Optional vendor-extensible headers — for example downstream tenant ids
    /// or feature flags the caller wants the callee to honor. Bounded to a few
    /// short string-string entries; transport implementations MAY refuse
    /// envelopes whose extension dictionary exceeds a configured size.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, string>? Extensions { get; init; }
}
