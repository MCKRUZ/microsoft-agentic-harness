using Application.AI.Common.Interfaces.AI;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// A turn lease whose loss the test decides, so a call site's handling of a stolen lease can be
/// exercised without a database.
/// </summary>
/// <remarks>
/// <para>
/// Neither real implementation can stand in here. <c>InProcessConversationTurnLease</c> never loses a
/// lease — its <c>LeaseLost</c> is permanently <see cref="CancellationToken.None"/> — and the durable
/// one only loses a lease when a second host takes it after an expiry, which is a database fixture,
/// not a unit test. Without this double, every call site's lost-lease branch is unreachable from the
/// suites that own those call sites: the linked token sources and the lost-lease catches could all be
/// deleted and the AgentHub tests would stay green, which is the failure mode this harness has
/// already shipped four times in a different guise.
/// </para>
/// </remarks>
internal sealed class ControllableTurnLease : IConversationTurnLease
{
    private readonly CancellationTokenSource _lost = new();

    /// <summary>Whether the handle handed out by this lease was disposed.</summary>
    internal bool Released { get; private set; }

    /// <summary>Simulates another host taking this conversation's lease mid-turn.</summary>
    internal void Steal() => _lost.Cancel();

    /// <inheritdoc />
    public Task<IConversationTurnLeaseHandle> AcquireAsync(
        string conversationId, CancellationToken ct = default) =>
        Task.FromResult<IConversationTurnLeaseHandle>(new Handle(this));

    private sealed class Handle(ControllableTurnLease owner) : IConversationTurnLeaseHandle
    {
        public CancellationToken LeaseLost => owner._lost.Token;

        public ValueTask DisposeAsync()
        {
            owner.Released = true;
            return ValueTask.CompletedTask;
        }
    }
}
