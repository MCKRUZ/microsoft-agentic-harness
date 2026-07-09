using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Compliance.EraseMyData;

/// <summary>
/// Fulfils a self-scoped right-to-erasure request. Reads the authenticated caller's identity from the
/// ambient <see cref="IKnowledgeScope"/> and erases only that owner's data via
/// <see cref="IErasureOrchestrator.EraseByOwnerAsync"/>, returning the <see cref="ErasureReceipt"/> as
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
    private readonly IErasureOrchestrator _erasureOrchestrator;
    private readonly ILogger<EraseMyDataCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="EraseMyDataCommandHandler"/> class.</summary>
    /// <param name="scope">Ambient knowledge scope supplying the authenticated caller's identity.</param>
    /// <param name="erasureOrchestrator">Cross-store right-to-erasure coordinator.</param>
    /// <param name="logger">Logger for the destructive-action audit trail.</param>
    public EraseMyDataCommandHandler(
        IKnowledgeScope scope,
        IErasureOrchestrator erasureOrchestrator,
        ILogger<EraseMyDataCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(erasureOrchestrator);
        ArgumentNullException.ThrowIfNull(logger);

        _scope = scope;
        _erasureOrchestrator = erasureOrchestrator;
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
            var receipt = await _erasureOrchestrator.EraseByOwnerAsync(ownerId, cancellationToken);

            _logger.LogInformation(
                "Right-to-erasure completed for owner {OwnerId}: request {RequestId}, "
                + "{Nodes} nodes, {Edges} edges, {Feedback} feedback weights, {Vectors} vector embeddings deleted.",
                ownerId, receipt.RequestId, receipt.NodesDeleted, receipt.EdgesDeleted,
                receipt.FeedbackWeightsDeleted, receipt.VectorEmbeddingsDeleted);

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
