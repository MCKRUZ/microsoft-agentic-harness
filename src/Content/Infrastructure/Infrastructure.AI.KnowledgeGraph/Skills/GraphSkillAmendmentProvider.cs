using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Skills;

/// <summary>
/// Manages skill amendments as graph nodes linked to synthetic skill index nodes.
/// Each amendment is stored as a <c>SkillAmendment</c>-typed node with an <c>amends</c>
/// edge pointing to the parent <c>Skill</c> node, enabling retrieval via
/// <see cref="IKnowledgeGraphStore.GetNeighborsAsync"/>. Amendments participate in the
/// compliance layer automatically via <see cref="GraphNode.OwnerId"/> and
/// <see cref="GraphNode.ExpiresAt"/>.
/// </summary>
public sealed class GraphSkillAmendmentProvider : ISkillAmendmentProvider
{
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly ILogger<GraphSkillAmendmentProvider> _logger;

    public GraphSkillAmendmentProvider(
        IKnowledgeGraphStore graphStore,
        ILogger<GraphSkillAmendmentProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(logger);
        _graphStore = graphStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AddAmendmentAsync(
        SkillAmendment amendment,
        CancellationToken cancellationToken = default)
    {
        // Ensure synthetic skill node exists
        var skillNodeId = $"skill:{amendment.SkillId}".ToLowerInvariant();
        var skillNode = new GraphNode
        {
            Id = skillNodeId,
            Name = amendment.SkillId,
            Type = "Skill"
        };
        await _graphStore.AddNodesAsync([skillNode], cancellationToken);

        // Store amendment as graph node
        var amendmentNode = new GraphNode
        {
            Id = amendment.Id,
            Name = $"Amendment: {amendment.SkillId}",
            Type = "SkillAmendment",
            Properties = new Dictionary<string, string>
            {
                ["SkillId"] = amendment.SkillId,
                ["Content"] = amendment.Content,
                ["LearnedFrom"] = amendment.LearnedFrom,
                ["CreatedAt"] = amendment.CreatedAt.ToString("O")
            },
            OwnerId = amendment.OwnerId
        };
        await _graphStore.AddNodesAsync([amendmentNode], cancellationToken);

        // Link amendment to skill
        var edge = new GraphEdge
        {
            Id = $"edge:{amendment.Id}:{skillNodeId}",
            SourceNodeId = amendment.Id,
            TargetNodeId = skillNodeId,
            Predicate = "amends",
            ChunkId = ""
        };
        await _graphStore.AddEdgesAsync([edge], cancellationToken);

        _logger.LogDebug("Added amendment {Id} for skill {SkillId}", amendment.Id, amendment.SkillId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SkillAmendment>> GetAmendmentsAsync(
        string skillId,
        CancellationToken cancellationToken = default)
    {
        var skillNodeId = $"skill:{skillId}".ToLowerInvariant();
        var neighbors = await _graphStore.GetNeighborsAsync(skillNodeId, maxDepth: 1, cancellationToken);

        return neighbors
            .Where(n => n.Type == "SkillAmendment" && n.Properties.GetValueOrDefault("SkillId") == skillId)
            .Select(n => new SkillAmendment
            {
                Id = n.Id,
                SkillId = n.Properties.GetValueOrDefault("SkillId", ""),
                Content = n.Properties.GetValueOrDefault("Content", ""),
                LearnedFrom = n.Properties.GetValueOrDefault("LearnedFrom", ""),
                CreatedAt = DateTimeOffset.TryParse(n.Properties.GetValueOrDefault("CreatedAt"), out var dt)
                    ? dt : DateTimeOffset.MinValue,
                OwnerId = n.OwnerId
            })
            .OrderBy(a => a.CreatedAt)
            .ToList();
    }

    /// <inheritdoc />
    public async Task RemoveAmendmentAsync(
        string amendmentId,
        CancellationToken cancellationToken = default)
    {
        await _graphStore.DeleteNodeAsync(amendmentId, cancellationToken);
        _logger.LogDebug("Removed amendment {Id}", amendmentId);
    }
}
