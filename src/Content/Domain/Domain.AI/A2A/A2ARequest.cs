using System.Text.Json.Serialization;

namespace Domain.AI.A2A;

/// <summary>
/// Wire-shape request for a single A2A call. Pairs an <see cref="A2AEnvelope"/>
/// (identity + correlation metadata) with the call payload (task description
/// and structured input).
/// </summary>
/// <remarks>
/// <para>
/// The shape is identical for in-process and cross-process calls — a consumer
/// that wires an in-process server today can flip it to cross-process tomorrow
/// without touching call sites. This is the core PR-7 invariant.
/// </para>
/// <para>
/// <see cref="Input"/> is a single JSON node (rather than a strongly-typed
/// payload) so the harness can route arbitrary skill inputs without adding a
/// new request type per skill. Callees are responsible for binding it to the
/// concrete shape their skill expects, and rejecting malformed payloads with
/// an <c>a2a.bad_request</c> error.
/// </para>
/// </remarks>
public sealed record A2ARequest
{
    /// <summary>
    /// Identity / correlation envelope. Required on every request.
    /// </summary>
    [JsonPropertyName("envelope")]
    public required A2AEnvelope Envelope { get; init; }

    /// <summary>
    /// Free-text task description for the callee. Mandatory: even
    /// structured-input calls carry a human-readable summary so observability
    /// dashboards can show what was asked without re-rendering the input
    /// payload.
    /// </summary>
    [JsonPropertyName("taskDescription")]
    public required string TaskDescription { get; init; }

    /// <summary>
    /// Optional structured input for the callee skill, serialized as a raw JSON
    /// document. Null when the callee only needs <see cref="TaskDescription"/>.
    /// </summary>
    [JsonPropertyName("input")]
    public System.Text.Json.JsonElement? Input { get; init; }
}
