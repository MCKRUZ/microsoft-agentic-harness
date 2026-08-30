using Application.AI.Common.Interfaces.ClaimVerification;
using Domain.AI.ClaimVerification;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.ClaimVerification;

/// <summary>
/// Default <see cref="IClaimVerifier"/> registered in every host: reports every claim as
/// <see cref="ClaimVerificationOutcome.Unverifiable"/> rather than throwing.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT the fail-fast throw-on-use shape of this subsystem's siblings
/// (<c>NotConfiguredRolloutRunner</c>, <c>NotConfiguredPatchProposer</c>): those gate a step
/// skill-training cannot meaningfully proceed without, so silently doing nothing would be worse
/// than a loud failure. Claim verification is additive quality assurance layered on an
/// already-advisory, already-fail-soft caller (<c>TrainSkillCommandHandler.EmitHarnessChangeSuggestionsAsync</c>
/// treats a faulty suggester the same way — logged, never fatal to the run) — so an unconfigured
/// host should degrade to "we couldn't check this," which
/// <see cref="ClaimVerificationOutcome.Unverifiable"/> already exists to express, not throw
/// something the caller would only catch and downgrade to the identical outcome anyway.
/// </para>
/// <para>
/// A host that wants real LLM-backed verification calls
/// <c>Infrastructure.AI.Evaluation.DependencyInjectionClaimVerification.AddClaimVerification</c>,
/// which registers the real implementation via a plain <c>AddSingleton</c> — last-registration-wins
/// over this type's <c>TryAddSingleton</c> default, the same mechanism
/// <c>NotConfiguredEvalRunner</c>'s own remarks document for <c>IEvalRunner</c>.
/// </para>
/// </remarks>
public sealed class NotConfiguredClaimVerifier : IClaimVerifier
{
    private readonly ILogger<NotConfiguredClaimVerifier> _logger;

    /// <summary>Initializes a new instance of the <see cref="NotConfiguredClaimVerifier"/> class.</summary>
    public NotConfiguredClaimVerifier(ILogger<NotConfiguredClaimVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<ClaimVerdict> VerifyAsync(Claim claim, string evidenceContent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(evidenceContent);

        _logger.LogInformation(
            "Claim verification is not configured in this host; reporting claim at '{Location}' as Unverifiable. " +
            "Call AddClaimVerification() to wire the real LLM-backed verifier.",
            claim.Location);

        return Task.FromResult(ClaimVerdict.Unverifiable(
            claim, "Claim verification is not configured in this host."));
    }
}
