using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Attestation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// Seals persisted governance records with the harness's existing HMAC attestation key
/// material — the same keys the sandbox tool-attestation path uses, sourced from User Secrets
/// or Key Vault and never from appsettings.
/// </summary>
/// <remarks>
/// <para>
/// Reuses <see cref="IAttestationService"/> rather than introducing a second signing key:
/// <see cref="IAttestationService.VerifyBoundAsync"/> already provides exactly the needed
/// primitive — recompute the content hash of the payload the caller currently holds, compare it
/// to the attested hash, then verify the HMAC over a payload that embeds both that hash and the
/// input hash. Passing the record's id as the attestation <c>Input</c> is what binds a seal to
/// one row: a seal lifted onto a different record carries the wrong <c>InputHash</c>, and
/// substituting the right one invalidates the signature.
/// </para>
/// <para>
/// <see cref="IAttestationService"/> is registered scoped, so each operation resolves it from a
/// fresh scope; sealing happens once per record write, a human-scale event. Verification
/// failures are logged and reported as <c>false</c> — never thrown — so a tampered row
/// quarantines that one record instead of failing a whole scan.
/// </para>
/// </remarks>
public sealed class AttestationGovernanceRecordSealer : IGovernanceRecordSealer
{
    /// <summary>
    /// The synthetic tool name the attestation payload is bound to, keeping governance seals in
    /// a distinct namespace from real tool-execution attestations.
    /// </summary>
    private const string SealSubject = "governance.record";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AttestationGovernanceRecordSealer> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="scopeFactory">Creates the scope the scoped attestation service resolves from.</param>
    /// <param name="logger">Structured logger.</param>
    public AttestationGovernanceRecordSealer(
        IServiceScopeFactory scopeFactory,
        ILogger<AttestationGovernanceRecordSealer> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GovernanceRecordSeal> SealAsync(string subjectId, string payloadJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var attestations = scope.ServiceProvider.GetRequiredService<IAttestationService>();

        // subjectId occupies the attestation Input slot, so its hash lands in the signed
        // payload and the seal is valid for this record only.
        var attestation = await attestations.SignAsync(
            AttestationRequest.Success(SealSubject, subjectId, payloadJson), ct);

        return new GovernanceRecordSeal(
            attestation.Signature,
            attestation.KeyVersion,
            attestation.Timestamp,
            attestation.InputHash,
            attestation.OutputHash ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(
        string subjectId, string payloadJson, GovernanceRecordSeal? seal, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);

        if (seal is null || string.IsNullOrEmpty(seal.Signature) || string.IsNullOrEmpty(seal.OutputHash))
        {
            // Fail-closed: an unsealed record is indistinguishable from one whose seal was
            // stripped, so it is never treated as trustworthy.
            _logger.LogError(
                "Persisted governance record {SubjectId} carries no usable seal; refusing to treat it as verified",
                subjectId);
            return false;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var attestations = scope.ServiceProvider.GetRequiredService<IAttestationService>();

            // The InputHash is derived from the subject the CALLER asked about, never taken
            // from the stored seal. A seal lifted from another record therefore fails the
            // signature check here even though its payload is byte-perfect.
            var expectedBinding = await attestations.SignAsync(
                AttestationRequest.Success(SealSubject, subjectId, payloadJson), ct);

            var attestation = new ToolExecutionAttestation
            {
                ToolName = SealSubject,
                InputHash = expectedBinding.InputHash,
                OutputHash = seal.OutputHash,
                Timestamp = seal.SignedAt,
                Signature = seal.Signature,
                KeyVersion = seal.KeyVersion
            };

            return await attestations.VerifyBoundAsync(attestation, payloadJson, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Governance record seal verification failed to run for {SubjectId}; treating the record as unverified",
                subjectId);
            return false;
        }
    }
}
