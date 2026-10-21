using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Compliance.EraseMyData;

/// <summary>
/// Fulfils a self-scoped right-to-erasure request. Reads the authenticated caller's identity from the
/// ambient <see cref="IKnowledgeScope"/> and erases only that owner's data via
/// <see cref="IPersonalDataErasure.EraseOwnerAsync"/>, returning the <see cref="ErasureReceipt"/> as
/// proof of compliance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-scope is enforced here, not taken from the request.</b> The command has no owner field; the
/// subject is always <see cref="IKnowledgeScope.UserId"/>. When no authenticated user scope is present
/// (anonymous context, or an authenticated token that carries no user id) the handler fails closed with
/// <see cref="Result{T}.Forbidden(string)"/> and erases nothing — a check that holds even if the
/// entry-point authorization were ever misconfigured. This fail-closed check lives in the handler (not a
/// pre-handler validator) on purpose: the handler is the innermost pipeline stage, so the denial is
/// still captured by <c>AuditTrailBehavior</c> as <c>AuditOutcome.Denied</c>. A validator that
/// short-circuited in <c>RequestValidationBehavior</c> — which is registered outer to the audit
/// behavior — would produce an unaudited denial, defeating the point of auditing a destructive action.
/// </para>
/// <para>
/// Store-level faults are caught, logged with full structured detail, and surfaced as a scrubbed
/// <see cref="Result{T}.Fail(string[])"/> so no internal path, connection string, or store exception
/// text reaches the caller. The <c>AuditTrailBehavior</c> records the outcome regardless.
/// </para>
/// </remarks>
public sealed class EraseMyDataCommandHandler
    : IRequestHandler<EraseMyDataCommand, Result<ErasureReceipt>>
{
    private readonly IKnowledgeScope _scope;
    private readonly IPersonalDataErasure _erasure;
    private readonly ILogger<EraseMyDataCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="EraseMyDataCommandHandler"/> class.</summary>
    /// <param name="scope">Ambient knowledge scope supplying the authenticated caller's identity.</param>
    /// <param name="erasure">Right-to-erasure across the knowledge graph and every registered store (#900).</param>
    /// <param name="logger">Logger for the destructive-action audit trail.</param>
    public EraseMyDataCommandHandler(
        IKnowledgeScope scope,
        IPersonalDataErasure erasure,
        ILogger<EraseMyDataCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(erasure);
        ArgumentNullException.ThrowIfNull(logger);

        _scope = scope;
        _erasure = erasure;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ErasureReceipt>> Handle(
        EraseMyDataCommand request,
        CancellationToken cancellationToken)
    {
        var ownerId = _scope.UserId;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            _logger.LogWarning(
                "Right-to-erasure request rejected: no authenticated user scope present.");
            return Result<ErasureReceipt>.Forbidden(
                "Right-to-erasure requires an authenticated user; no user scope is present.");
        }

        _logger.LogInformation(
            "Right-to-erasure requested for owner {OwnerId} (tenant {TenantId}).",
            ownerId, _scope.TenantId);

        try
        {
            var receipt = await _erasure.EraseOwnerAsync(ownerId, cancellationToken);

            _logger.LogInformation(
                "Right-to-erasure completed for owner {OwnerId}: request {RequestId}, completeness {Completeness}, "
                + "{Nodes} nodes, {Edges} edges, {Stores} other stores reached.",
                ownerId, receipt.RequestId, receipt.Completeness, receipt.NodesDeleted, receipt.EdgesDeleted,
                receipt.Stores.Count);

            return Result<ErasureReceipt>.Success(receipt);
        }
        catch (Exception ex)
        {
            // Scrub the outbound message: store exceptions can carry connection strings and paths.
            _logger.LogError(ex,
                "Right-to-erasure failed for owner {OwnerId}.", ownerId);
            return Result<ErasureReceipt>.Fail(
                "Erasure could not be completed. The failure has been logged for investigation.");
        }
    }
}
