using Application.AI.Common.Interfaces.Skills;

namespace Infrastructure.AI.Skills;

/// <summary>
/// <see cref="AsyncLocal{T}"/>-backed implementation of
/// <see cref="ICurrentSkillAccessor"/>. The value flows down the async call
/// chain so per-skill policy resolvers (notably the egress allowlist resolver)
/// can read it from anywhere within the skill's logical scope without
/// threading the identifier through every API.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton because the backing store is per-async-flow, not
/// per-DI-scope. Concurrent agent turns each see their own value; nested
/// activations restore the previous identifier on disposal.
/// </para>
/// </remarks>
public sealed class CurrentSkillAccessor : ICurrentSkillAccessor
{
    private static readonly AsyncLocal<string?> Slot = new();

    /// <inheritdoc />
    public string? CurrentSkillId => Slot.Value;

    /// <inheritdoc />
    public IDisposable BeginScope(string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var previous = Slot.Value;
        Slot.Value = skillId;
        return new Restorer(previous);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Restorer(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Slot.Value = _previous;
        }
    }
}
