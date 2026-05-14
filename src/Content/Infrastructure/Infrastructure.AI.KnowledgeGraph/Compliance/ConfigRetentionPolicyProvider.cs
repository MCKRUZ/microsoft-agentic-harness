using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph.Compliance;

/// <summary>
/// Resolves retention policies from <c>GraphRagConfig.RetentionPolicies</c> configuration.
/// Entity types not listed in config get indefinite retention (never expire).
/// </summary>
public sealed class ConfigRetentionPolicyProvider : IRetentionPolicyProvider
{
    private readonly IOptionsMonitor<AppConfig> _configMonitor;

    public ConfigRetentionPolicyProvider(IOptionsMonitor<AppConfig> configMonitor)
    {
        ArgumentNullException.ThrowIfNull(configMonitor);
        _configMonitor = configMonitor;
    }

    /// <inheritdoc />
    public RetentionPolicy GetPolicy(string entityType)
    {
        var policies = _configMonitor.CurrentValue.AI.Rag.GraphRag.RetentionPolicies;

        if (policies.TryGetValue(entityType, out var period))
        {
            return new RetentionPolicy
            {
                EntityType = entityType,
                RetentionPeriod = period,
                AllowIndefinite = false
            };
        }

        return new RetentionPolicy
        {
            EntityType = entityType,
            RetentionPeriod = TimeSpan.Zero,
            AllowIndefinite = true
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<RetentionPolicy> GetAllPolicies()
    {
        return _configMonitor.CurrentValue.AI.Rag.GraphRag.RetentionPolicies
            .Select(kvp => new RetentionPolicy
            {
                EntityType = kvp.Key,
                RetentionPeriod = kvp.Value,
                AllowIndefinite = false
            })
            .ToList();
    }
}
