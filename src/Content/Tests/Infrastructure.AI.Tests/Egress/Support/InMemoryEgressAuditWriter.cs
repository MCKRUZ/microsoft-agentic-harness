using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Egress;
using Domain.AI.Identity;
using Domain.Common;

namespace Infrastructure.AI.Tests.Egress.Support;

internal sealed class InMemoryEgressAuditWriter : IEgressAuditWriter
{
    public ConcurrentQueue<(EgressDecision Decision, AgentIdentity Identity)> Entries { get; } = new();

    public Task AppendAsync(EgressDecision decision, AgentIdentity identity, CancellationToken cancellationToken)
    {
        Entries.Enqueue((decision, identity));
        return Task.CompletedTask;
    }

    public Task<Result<IReadOnlyList<EgressAuditRecord>>> GetRecordsAsync(
        EgressAuditQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<IReadOnlyList<EgressAuditRecord>>.Success(Array.Empty<EgressAuditRecord>()));
}
