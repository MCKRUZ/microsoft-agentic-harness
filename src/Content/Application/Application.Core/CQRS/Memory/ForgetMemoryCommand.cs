using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Memory;

/// <summary>
/// Removes a fact from the caller's cross-session memory (session cache and durable graph).
/// Self-scoped by construction: the node id is derived server-side from the ambient knowledge
/// scope plus the supplied key, so a caller can only ever forget their own facts. Forgetting a
/// key that does not exist succeeds idempotently.
/// </summary>
public sealed record ForgetMemoryCommand : IRequest<Result>
{
    /// <summary>The key of the memory entry to forget.</summary>
    public required string Key { get; init; }
}
