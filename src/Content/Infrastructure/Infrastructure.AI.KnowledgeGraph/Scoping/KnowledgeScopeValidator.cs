using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Scoping;
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

        // Null target tenant means the node/edge has no tenant metadata — deny access
        // when isolation is enabled, since "unknown tenant" is not "any tenant".
        if (targetTenantId is null)
            return false;

        if (scope.TenantId is null)
            return false;

        // Compare via the shared canonical form (trimmed, invariant-lowercase) so this gate agrees
        // exactly with how the storage backends persist and filter tenant identity. A raw
        // OrdinalIgnoreCase compare here would authorize access the case-sensitive backend filters
        // then fail to honor — the drift this canonicalization closes.
        return ScopeIdentity.AreSame(scope.TenantId, targetTenantId);
    }

    /// <inheritdoc />
    public bool CanAccessDataset(
        IKnowledgeScope scope,
        string datasetOwnerId)
    {
        if (!_configMonitor.CurrentValue.AI.Rag.GraphRag.MultiTenantIsolation)
            return true;

        // An absent (null/empty/whitespace) dataset owner is never an authorizable dataset: it denotes
        // a shared/global (owner-null) record, whose access is decided upstream by the caller
        // (TenantIsolatedGraphStore short-circuits `ownerId is null`), not by a per-owner authorization.
        // Guard first so ScopeIdentity.AreSame — which treats two absent ids as equal — cannot authorize
        // a null-UserId caller against an absent owner (the old OrdinalIgnoreCase gate denied that).
        if (ScopeIdentity.Canonicalize(datasetOwnerId) is null)
            return false;

        // Owner-level isolation: the owner id is a user id, so access is granted only when the
        // caller is that user. We deliberately do NOT compare scope.TenantId against the owner id —
        // that conflates two distinct id namespaces (a tenant id is not a user id) and would grant
        // cross-user access on any string collision. Tenant-level sharing requires a real TenantId
        // on the node model and is deferred to the tenant-isolation follow-up.
        //
        // Compare via the shared canonical form (trimmed, invariant-lowercase) so this gate agrees
        // exactly with the case-sensitive owner filters in every storage backend. Otherwise the gate
        // could authorize an erasure the store then fails to match, silently leaving the subject's
        // data in place.
        return ScopeIdentity.AreSame(scope.UserId, datasetOwnerId);
    }
}
