using Domain.AI.Attestation;
using Application.AI.Common.Interfaces.Attestation;
using Moq;

namespace Infrastructure.AI.Tests.Sandbox.Support;

/// <summary>
/// Shared Moq setup for capturing the non-failure <see cref="AttestationRequest"/> a session
/// factory signs, so tests can assert on the request's <c>Input</c> JSON (e.g.
/// <c>capabilitiesEnforcedBy</c>, the resolved container image) without hand-rolling the same
/// stub-and-capture block per test.
/// </summary>
internal static class AttestationCaptureMockExtensions
{
    /// <summary>
    /// Stubs <paramref name="attestation"/> to sign every non-failure request and capture it.
    /// </summary>
    /// <param name="attestation">The mocked <see cref="IAttestationService"/> under test.</param>
    /// <returns>A getter for the most recently captured request, or <c>null</c> if none signed yet.</returns>
    public static Func<AttestationRequest?> CaptureNonFailureAttestation(this Mock<IAttestationService> attestation)
    {
        AttestationRequest? signed = null;
        attestation
            .Setup(x => x.SignAsync(It.Is<AttestationRequest>(r => !r.IsFailure), It.IsAny<CancellationToken>()))
            .Callback<AttestationRequest, CancellationToken>((r, _) => signed = r)
            .ReturnsAsync((AttestationRequest r, CancellationToken _) => new ToolExecutionAttestation
            {
                ToolName = r.ToolName,
                InputHash = "test-hash",
                Timestamp = DateTimeOffset.UtcNow,
                Signature = "test-sig",
                KeyVersion = "v1",
                IsFailureAttestation = false
            });
        return () => signed;
    }
}
