using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Egress;
using Domain.AI.Identity;

namespace Infrastructure.AI.Tests.Egress.Support;

internal sealed class InMemoryEgressAuditWriter : IEgressAuditWriter
{
    public ConcurrentQueue<(EgressDecision Decision, AgentIdentity Identity)> Entries { get; } = new();

    public Task AppendAsync(EgressDecision decision, AgentIdentity identity, CancellationToken cancellationToken)
    {
        Entries.Enqueue((decision, identity));
        return Task.CompletedTask;
    }
}
