using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Memory;

/// <summary>
/// Forgets a fact via <see cref="IKnowledgeMemory.ForgetAsync"/>. The underlying graph delete is
/// a documented no-op for a missing node, so forgetting an unknown key returns success — the
/// desired end state ("this key holds nothing") already holds, mirroring the harness's
/// delete-unknown → success idempotency convention.
/// </summary>
public sealed class ForgetMemoryCommandHandler : IRequestHandler<ForgetMemoryCommand, Result>
{
    private readonly IKnowledgeMemory _memory;
    private readonly ILogger<ForgetMemoryCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="ForgetMemoryCommandHandler"/> class.</summary>
    /// <param name="memory">The scope-aware cross-session memory service.</param>
    /// <param name="logger">Logger for recording forget operations.</param>
    public ForgetMemoryCommandHandler(
        IKnowledgeMemory memory,
        ILogger<ForgetMemoryCommandHandler> logger)
    {
        _memory = memory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> Handle(ForgetMemoryCommand request, CancellationToken cancellationToken)
    {
        await _memory.ForgetAsync(request.Key, cancellationToken);

        _logger.LogInformation("Memory forget: Key={Key}", request.Key);

        return Result.Success();
    }
}
