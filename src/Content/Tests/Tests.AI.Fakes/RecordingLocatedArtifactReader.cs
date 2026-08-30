using Application.AI.Common.Interfaces.ClaimVerification;

namespace Tests.AI.Fakes;

/// <summary>
/// Scriptable <see cref="ILocatedArtifactReader"/> fake: delegates <see cref="TryReadAsync"/> to a
/// caller-supplied handler and counts invocations.
/// </summary>
public sealed class RecordingLocatedArtifactReader : ILocatedArtifactReader
{
    private readonly Func<string, CancellationToken, Task<string?>> _handler;
    private int _callCount;

    /// <summary>Initializes a new instance of the <see cref="RecordingLocatedArtifactReader"/> class.</summary>
    public RecordingLocatedArtifactReader(Func<string, CancellationToken, Task<string?>> handler)
    {
        _handler = handler;
    }

    /// <summary>Number of times <see cref="TryReadAsync"/> has been called.</summary>
    public int CallCount => _callCount;

    /// <inheritdoc />
    public Task<string?> TryReadAsync(string location, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return _handler(location, cancellationToken);
    }
}
