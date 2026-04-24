namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Validates whether the current <see cref="IKnowledgeScope"/> has permission to
/// access a specific dataset or tenant's knowledge graph. Used by
/// <c>TenantIsolatedGraphStore</c> to enforce access boundaries.
/// </summary>
/// <remarks>
/// Implementations should check scope properties against the dataset's ownership
/// and sharing configuration. The default implementation allows access when:
/// <list type="bullet">
///   <item>Multi-tenant isolation is disabled (single-tenant mode).</item>
///   <item>The user owns the dataset (<see cref="IKnowledgeScope.UserId"/> matches owner).</item>
///   <item>The user belongs to the same tenant as the dataset owner.</item>
/// </list>
/// </remarks>
public interface IKnowledgeScopeValidator
{
    /// <summary>
    /// Validates whether the current scope has access to the target tenant and dataset.
    /// </summary>
    /// <param name="scope">The current knowledge scope.</param>
    /// <param name="targetTenantId">The tenant ID being accessed.</param>
    /// <param name="targetDatasetId">The dataset ID being accessed. Null for tenant-wide operations.</param>
    /// <returns><c>true</c> if access is allowed; <c>false</c> if denied.</returns>
    bool ValidateAccess(
        IKnowledgeScope scope,
        string? targetTenantId,
        string? targetDatasetId = null);

    /// <summary>
    /// Checks whether the scope can access a specific dataset by owner.
    /// </summary>
    /// <param name="scope">The current knowledge scope.</param>
    /// <param name="datasetOwnerId">The user ID of the dataset owner.</param>
    /// <returns><c>true</c> if the scope can access the dataset; <c>false</c> otherwise.</returns>
    bool CanAccessDataset(
        IKnowledgeScope scope,
        string datasetOwnerId);
}
