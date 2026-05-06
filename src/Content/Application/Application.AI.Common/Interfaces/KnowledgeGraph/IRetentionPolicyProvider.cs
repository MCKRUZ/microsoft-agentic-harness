using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Resolves retention policies per entity type. Default implementation reads from
/// <c>GraphRagConfig.RetentionPolicies</c>. Enterprise consumers can override with
/// database-backed or policy-engine implementations.
/// </summary>
public interface IRetentionPolicyProvider
{
    /// <summary>Get the retention policy for a specific entity type. Returns indefinite policy for unknown types.</summary>
    RetentionPolicy GetPolicy(string entityType);

    /// <summary>Get all configured retention policies.</summary>
    IReadOnlyList<RetentionPolicy> GetAllPolicies();
}
