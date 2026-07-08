namespace Domain.AI.Attestation;

/// <summary>
/// Describes a single tool execution to be attested. Carries the discriminator
/// (<see cref="IsFailure"/>) plus every field any signing variant needs, so one
/// signing method replaces the former family of <c>Sign*</c> overloads without
/// changing the produced attestation payloads.
/// </summary>
/// <remarks>
/// The signing payload shape is selected from this request's fields:
/// <list type="bullet">
/// <item><description>Success (<see cref="IsFailure"/> = false): the output content hash is bound; <see cref="Output"/> is required.</description></item>
/// <item><description>Failure with no produced output (<see cref="Output"/> = null): a failure payload with a <c>null</c> output slot.</description></item>
/// <item><description>Failure that produced output before failing (<see cref="Output"/> set): the produced output's content hash occupies the output slot, so the returned output stays bound to the signed record.</description></item>
/// </list>
/// A non-null <see cref="EgressDigest"/> appends the egress digest to any of the
/// above shapes. Prefer the <see cref="Success"/> and <see cref="Failure"/> factory
/// methods over the initializer for call-site clarity.
/// </remarks>
public sealed record AttestationRequest
{
    /// <summary>Name of the tool that was executed.</summary>
    public required string ToolName { get; init; }

    /// <summary>Serialized tool input.</summary>
    public required string Input { get; init; }

    /// <summary>
    /// Whether this attestation records a failed execution. When true,
    /// <see cref="FailureReason"/> must be supplied.
    /// </summary>
    public bool IsFailure { get; init; }

    /// <summary>
    /// Serialized tool output. Required for a success attestation. For a failure
    /// attestation it is optional: supply it when the tool produced output before
    /// failing (so the produced bytes are bound into the signed record), or leave it
    /// null when execution failed without producing any output.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// Description of the failure. Required when <see cref="IsFailure"/> is true;
    /// ignored otherwise.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// SHA-256 digest of the egress decisions recorded during sandbox preflight.
    /// Null when no preflight ran; a non-null value binds the digest into the
    /// signed payload.
    /// </summary>
    public string? EgressDigest { get; init; }

    /// <summary>
    /// Creates a request for a successful tool execution, binding the produced
    /// output's content hash into the attestation.
    /// </summary>
    /// <param name="toolName">Name of the tool that was executed.</param>
    /// <param name="input">Serialized tool input.</param>
    /// <param name="output">Serialized tool output.</param>
    /// <param name="egressDigest">Optional SHA-256 digest of the recorded egress decisions.</param>
    /// <returns>A success attestation request.</returns>
    public static AttestationRequest Success(string toolName, string input, string output, string? egressDigest = null)
        => new()
        {
            ToolName = toolName,
            Input = input,
            IsFailure = false,
            Output = output,
            EgressDigest = egressDigest
        };

    /// <summary>
    /// Creates a request for a failed tool execution.
    /// </summary>
    /// <param name="toolName">Name of the tool that was executed.</param>
    /// <param name="input">Serialized tool input.</param>
    /// <param name="failureReason">Description of the failure.</param>
    /// <param name="output">
    /// Output produced before the failure, or null when none was produced. When
    /// supplied, its content hash is bound into the signed payload.
    /// </param>
    /// <param name="egressDigest">Optional SHA-256 digest of the recorded egress decisions.</param>
    /// <returns>A failure attestation request.</returns>
    public static AttestationRequest Failure(
        string toolName,
        string input,
        string failureReason,
        string? output = null,
        string? egressDigest = null)
        => new()
        {
            ToolName = toolName,
            Input = input,
            IsFailure = true,
            FailureReason = failureReason,
            Output = output,
            EgressDigest = egressDigest
        };
}
