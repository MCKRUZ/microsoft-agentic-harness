using Application.AI.Common.Interfaces.Verification;
using Domain.AI.Verification;

namespace Tests.AI.Fakes;

/// <summary>
/// Scriptable <see cref="IObligationVerifier"/> fake: delegates <see cref="VerifyAsync"/> to a
/// caller-supplied handler and counts invocations. Shared by <c>Application.AI.Common.Tests</c> and
/// <c>Infrastructure.AI.Evaluation.Tests</c>, which each had their own byte-identical copy before
/// this extraction.
/// </summary>
public sealed class RecordingObligationVerifier : IObligationVerifier
{
    private readonly Func<Obligation, string, CancellationToken, Task<VerificationVerdict>> _handler;
    private int _callCount;

    /// <summary>Initializes a new instance of the <see cref="RecordingObligationVerifier"/> class.</summary>
    public RecordingObligationVerifier(Func<Obligation, string, CancellationToken, Task<VerificationVerdict>> handler)
    {
        _handler = handler;
    }

    /// <summary>Number of times <see cref="VerifyAsync"/> has been called.</summary>
    public int CallCount => _callCount;

    /// <inheritdoc />
    public Task<VerificationVerdict> VerifyAsync(Obligation obligation, string artifactContent, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return _handler(obligation, artifactContent, cancellationToken);
    }
}
