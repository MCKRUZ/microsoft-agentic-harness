using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Memory;

/// <summary>
/// Stores a fact via <see cref="IKnowledgeMemory.RememberAsync"/> and surfaces the write gate's
/// decision as a <see cref="RememberMemoryResult"/>. Thin by design: gating, trust classification,
/// scope namespacing, and persistence all live behind the memory abstraction — this handler only
/// projects the decision into the caller-facing result.
/// </summary>
public sealed class RememberMemoryCommandHandler
    : IRequestHandler<RememberMemoryCommand, Result<RememberMemoryResult>>
{
    private readonly IKnowledgeMemory _memory;
    private readonly ILogger<RememberMemoryCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="RememberMemoryCommandHandler"/> class.</summary>
    /// <param name="memory">The scope-aware cross-session memory service.</param>
    /// <param name="logger">Logger for recording write outcomes (never content).</param>
    public RememberMemoryCommandHandler(
        IKnowledgeMemory memory,
        ILogger<RememberMemoryCommandHandler> logger)
    {
        _memory = memory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<RememberMemoryResult>> Handle(
        RememberMemoryCommand request, CancellationToken cancellationToken)
    {
        var decision = await _memory.RememberAsync(
            request.Key, request.Content, request.EntityType, cancellationToken);

        // Key + outcome only — remembered content may be sensitive and is never logged here.
        _logger.LogInformation(
            "Memory write: Key={Key}, EntityType={EntityType}, Outcome={Outcome}",
            request.Key, request.EntityType, decision.Outcome);

        return Result<RememberMemoryResult>.Success(new RememberMemoryResult
        {
            Outcome = decision.Outcome,
            Reason = decision.Reason
        });
    }
}
