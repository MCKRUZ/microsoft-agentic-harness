using System.Text.Json.Serialization;

namespace Domain.AI.A2A;

/// <summary>
/// Wire-shape response for a single A2A call. Carries a success-or-error
/// discriminator plus the correlation id from the originating envelope.
/// </summary>
/// <remarks>
/// <para>
/// The shape is intentionally narrow: a successful call returns an
/// <see cref="Output"/> JSON document; a failed call returns an
/// <see cref="ErrorCode"/> (stable <c>a2a.*</c> identifier) and a sanitized
/// <see cref="ErrorMessage"/>. Detailed exception context is logged via
/// structured logging on the server side and never sent over the wire — A2A
/// errors are treated like CQRS <c>Result.Fail</c> codes.
/// </para>
/// </remarks>
public sealed record A2AResponse
{
    /// <summary>
    /// Correlation id echoed back from the request envelope. Required so the
    /// caller can match an asynchronous response to its outbound call without
    /// trusting the transport channel.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Indicates whether the call succeeded. When <c>false</c>, the
    /// <see cref="ErrorCode"/> field MUST be populated and <see cref="Output"/>
    /// MUST be null. When <c>true</c>, <see cref="ErrorCode"/> MUST be null.
    /// </summary>
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    /// <summary>
    /// Successful callee output as a raw JSON document. Null when
    /// <see cref="Success"/> is <c>false</c>.
    /// </summary>
    [JsonPropertyName("output")]
    public System.Text.Json.JsonElement? Output { get; init; }

    /// <summary>
    /// Stable error code such as <c>a2a.auth_rejected</c>, <c>a2a.timeout</c>,
    /// <c>a2a.bad_request</c>, or <c>a2a.skill_failed</c>. Null on success.
    /// Surfaces as <c>gen_ai.a2a.error.code</c> on the spans.
    /// </summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Optional human-readable error message. Sanitized server-side: never
    /// includes stack traces, secrets, or internal paths.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Convenience factory for a successful response.
    /// </summary>
    /// <param name="correlationId">Correlation id from the request envelope.</param>
    /// <param name="output">Successful output JSON document.</param>
    public static A2AResponse Ok(string correlationId, System.Text.Json.JsonElement output) =>
        new() { CorrelationId = correlationId, Success = true, Output = output };

    /// <summary>
    /// Convenience factory for a failed response.
    /// </summary>
    /// <param name="correlationId">Correlation id from the request envelope.</param>
    /// <param name="errorCode">Stable <c>a2a.*</c> error code.</param>
    /// <param name="errorMessage">Sanitized human-readable error message.</param>
    public static A2AResponse Fail(string correlationId, string errorCode, string? errorMessage = null) =>
        new()
        {
            CorrelationId = correlationId,
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
}
