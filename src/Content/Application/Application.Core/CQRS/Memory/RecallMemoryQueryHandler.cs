using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Memory;

/// <summary>
/// Recalls facts via <see cref="IKnowledgeMemory.RecallAsync"/> and projects the matched graph
/// nodes to the wire-safe <see cref="MemoryEntry"/> shape, dropping owner/tenant ids, provenance,
/// trust markers, and any other node internals.
/// </summary>
public sealed class RecallMemoryQueryHandler
    : IRequestHandler<RecallMemoryQuery, Result<IReadOnlyList<MemoryEntry>>>
{
    private readonly IKnowledgeMemory _memory;
    private readonly ILogger<RecallMemoryQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="RecallMemoryQueryHandler"/> class.</summary>
    /// <param name="memory">The scope-aware cross-session memory service.</param>
    /// <param name="logger">Logger for recording recall statistics (never content).</param>
    public RecallMemoryQueryHandler(
        IKnowledgeMemory memory,
        ILogger<RecallMemoryQueryHandler> logger)
    {
        _memory = memory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<MemoryEntry>>> Handle(
        RecallMemoryQuery request, CancellationToken cancellationToken)
    {
        var nodes = await _memory.RecallAsync(request.Query, request.MaxResults, cancellationToken);

        // "content" is the property key KnowledgeMemoryService stamps fact content under; corpus
        // entity nodes matched by graph traversal carry no such property and project as empty.
        IReadOnlyList<MemoryEntry> entries = nodes
            .Select(n => new MemoryEntry
            {
                Key = n.Name,
                Content = n.Properties.GetValueOrDefault("content", string.Empty)!,
                EntityType = n.Type,
                CreatedAt = n.CreatedAt
            })
            .ToList();

        _logger.LogDebug("Memory recall returned {Count} entries", entries.Count);

        return Result<IReadOnlyList<MemoryEntry>>.Success(entries);
    }
}
