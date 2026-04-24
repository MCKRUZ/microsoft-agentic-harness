using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph.Scoping;

/// <summary>
/// Default <see cref="IKnowledgeScopeValidator"/> that enforces tenant and dataset
/// access boundaries. When multi-tenant isolation is disabled, all access is allowed.
/// </summary>
/// <remarks>
/// Access rules:
/// <list type="bullet">
///   <item>If <c>MultiTenantIsolation</c> is disabled, always allows access.</item>
///   <item>Tenant match: scope's tenant must match the target tenant.</item>
///   <item>Dataset ownership: scope's user must match the dataset owner,
///         or be in the same tenant.</item>
/// </list>
/// </remarks>
public sealed class KnowledgeScopeValidator : IKnowledgeScopeValidator
{
    private readonly IOptionsMonitor<AppConfig> _configMonitor;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeScopeValidator"/> class.
    /// </summary>
    /// <param name="configMonitor">Application configuration for isolation settings.</param>
    public KnowledgeScopeValidator(IOptionsMonitor<AppConfig> configMonitor)
    {
        ArgumentNullException.ThrowIfNull(configMonitor);
        _configMonitor = configMonitor;
    }

    /// <inheritdoc />
    public bool ValidateAccess(
        IKnowledgeScope scope,
        string? targetTenantId,
        string? targetDatasetId = null)
    {
        if (!_configMonitor.CurrentValue.AI.Rag.GraphRag.MultiTenantIsolation)
            return true;

        if (targetTenantId is null)
            return true;

        if (scope.TenantId is null)
            return false;

        return string.Equals(scope.TenantId, targetTenantId, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool CanAccessDataset(
        IKnowledgeScope scope,
        string datasetOwnerId)
    {
        if (!_configMonitor.CurrentValue.AI.Rag.GraphRag.MultiTenantIsolation)
            return true;

        if (string.Equals(scope.UserId, datasetOwnerId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (scope.TenantId is not null && scope.DatasetOwnerId is not null)
            return string.Equals(scope.TenantId, scope.DatasetOwnerId, StringComparison.OrdinalIgnoreCase);

        return false;
    }
}
