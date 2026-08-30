using Application.AI.Common.Interfaces.ClaimVerification;
using Domain.AI.ClaimVerification;

namespace Tests.AI.Fakes;

/// <summary>
/// Scriptable <see cref="IClaimVerifier"/> fake: delegates <see cref="VerifyAsync"/> to a
/// caller-supplied handler and counts invocations.
/// </summary>
public sealed class RecordingClaimVerifier : IClaimVerifier
{
    private readonly Func<Claim, string, CancellationToken, Task<ClaimVerdict>> _handler;
    private int _callCount;

    /// <summary>Initializes a new instance of the <see cref="RecordingClaimVerifier"/> class.</summary>
    public RecordingClaimVerifier(Func<Claim, string, CancellationToken, Task<ClaimVerdict>> handler)
    {
        _handler = handler;
    }

    /// <summary>Number of times <see cref="VerifyAsync"/> has been called.</summary>
    public int CallCount => _callCount;

    /// <inheritdoc />
    public Task<ClaimVerdict> VerifyAsync(Claim claim, string evidenceContent, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return _handler(claim, evidenceContent, cancellationToken);
    }
}
