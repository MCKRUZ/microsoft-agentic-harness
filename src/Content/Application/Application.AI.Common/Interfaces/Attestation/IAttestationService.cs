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
    /// Signs a successful tool execution, producing an attestation with input and output hashes.
    /// </summary>
    /// <param name="toolName">Name of the tool that was executed.</param>
    /// <param name="input">Serialized tool input.</param>
    /// <param name="output">Serialized tool output.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A signed attestation.</returns>
    Task<ToolExecutionAttestation> SignAsync(string toolName, string input, string output, CancellationToken ct);

    /// <summary>
    /// Signs a failed tool execution, producing a failure attestation with a reason but no output hash.
    /// </summary>
    /// <param name="toolName">Name of the tool that was executed.</param>
    /// <param name="input">Serialized tool input.</param>
    /// <param name="failureReason">Description of the failure.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A signed failure attestation.</returns>
    Task<ToolExecutionAttestation> SignFailureAsync(string toolName, string input, string failureReason, CancellationToken ct);

    /// <summary>
    /// Verifies the HMAC signature of an existing attestation.
    /// </summary>
    /// <param name="attestation">The attestation to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the attestation signature is valid; false otherwise.</returns>
    Task<bool> VerifyAsync(ToolExecutionAttestation attestation, CancellationToken ct);
}
