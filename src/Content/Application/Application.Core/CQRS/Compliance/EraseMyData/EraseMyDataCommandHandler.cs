using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Compliance.EraseMyData;

/// <summary>
/// Fulfils a self-scoped right-to-erasure request. Reads the authenticated caller's identity from the ambient
/// <see cref="IKnowledgeScope"/> and erases only that person's data through <see cref="AuditedErasure"/>, returning the
/// <see cref="ErasureReceipt"/> as proof of compliance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-scope is enforced here, not taken from the request.</b> The command has no owner field; the subject is always
/// <see cref="IKnowledgeScope.UserId"/>. When no authenticated user scope is present (anonymous context, or an
/// authenticated token that carries no user id) the handler fails closed with <see cref="Result{T}.Forbidden(string)"/>,
/// erases nothing, and records the denial — a check that holds even if the entry-point authorization were ever
/// misconfigured.
/// </para>
/// <para>
/// Nothing here logs the caller's id: it is the identity this request removes.
/// </para>
/// </remarks>
public sealed class EraseMyDataCommandHandler
    : IRequestHandler<EraseMyDataCommand, Result<ErasureReceipt>>
{
    private const string NoUserScope = "Right-to-erasure requires an authenticated user; no user scope is present.";

    private readonly IKnowledgeScope _scope;
    private readonly AuditedErasure _erasure;
    private readonly ILogger<EraseMyDataCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="EraseMyDataCommandHandler"/> class.</summary>
    /// <param name="scope">Ambient knowledge scope supplying the authenticated caller's identity.</param>
    /// <param name="erasure">Runs the erasure and writes its audit record (#900).</param>
    /// <param name="logger">Logger for refused requests.</param>
    public EraseMyDataCommandHandler(
        IKnowledgeScope scope,
        AuditedErasure erasure,
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
            _logger.LogWarning("Right-to-erasure request rejected: no authenticated user scope present.");
            await _erasure.RecordDeniedAsync(ErasureActor.DataSubject, nameof(EraseMyDataCommand), NoUserScope);
            return Result<ErasureReceipt>.Forbidden(NoUserScope);
        }

        return await _erasure.EraseAsync(
            ownerId, ErasureActor.DataSubject, nameof(EraseMyDataCommand), cancellationToken);
    }
}
