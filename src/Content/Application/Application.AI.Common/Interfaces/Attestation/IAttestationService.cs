using Domain.AI.Attestation;

namespace Application.AI.Common.Interfaces.Attestation;

/// <summary>
/// Creates and verifies HMAC-signed attestations of tool execution.
/// Signing keys are sourced from User Secrets (development) or Key Vault (production),
/// never from appsettings.json.
/// </summary>
public interface IAttestationService
{
    /// <summary>
    /// Signs a tool execution described by <paramref name="request"/>, producing a
    /// tamper-evident attestation. This single entry point replaces the former family of
    /// <c>Sign*</c> overloads: the payload shape (success, failure, failure-with-output, and
    /// the optional egress-digest binding) is selected from the request's fields. See
    /// <see cref="AttestationRequest"/> for the field-to-shape mapping.
    /// </summary>
    /// <remarks>
    /// Payload shapes coexist so attestations signed under any earlier overload remain
    /// verifiable: field presence on the produced record (output hash, egress digest, the
    /// failure flag) is the discriminator during verification.
    /// </remarks>
    /// <param name="request">The execution to attest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A signed attestation.</returns>
    Task<ToolExecutionAttestation> SignAsync(AttestationRequest request, CancellationToken ct);

    /// <summary>
    /// Verifies the HMAC signature of an existing attestation.
    /// </summary>
    /// <remarks>
    /// This validates only the attestation's own fields. It does NOT prove that a separately
    /// stored output still matches what was signed — use
    /// <see cref="VerifyBoundAsync"/> when the actual output bytes are available.
    /// </remarks>
    /// <param name="attestation">The attestation to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the attestation signature is valid; false otherwise.</returns>
    Task<bool> VerifyAsync(ToolExecutionAttestation attestation, CancellationToken ct);

    /// <summary>
    /// Verifies that an attestation is authentic AND that it was signed over exactly the
    /// given output. Recomputes the content hash of <paramref name="actualOutput"/>, compares
    /// it to the attested output hash, then verifies the HMAC signature. This binds the
    /// attestation to the real output bytes: a result whose output was tampered after signing
    /// fails this check even though the signature alone would still validate.
    /// </summary>
    /// <param name="attestation">The attestation to verify.</param>
    /// <param name="actualOutput">The output the caller currently holds for this execution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// True only when the attestation recorded an output hash, that hash matches
    /// <paramref name="actualOutput"/>, and the signature is valid.
    /// </returns>
    Task<bool> VerifyBoundAsync(ToolExecutionAttestation attestation, string actualOutput, CancellationToken ct);
}
