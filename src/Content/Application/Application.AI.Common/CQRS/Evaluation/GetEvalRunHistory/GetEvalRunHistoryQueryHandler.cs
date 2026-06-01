using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.Evaluation.GetEvalRunHistory;

/// <summary>
/// Handles <see cref="GetEvalRunHistoryQuery"/> by delegating to
/// <see cref="IEvalRunStore.GetRecentAsync"/>.
/// </summary>
public sealed class GetEvalRunHistoryQueryHandler
    : IRequestHandler<GetEvalRunHistoryQuery, Result<IReadOnlyList<EvalRunSummary>>>
{
    private readonly IEvalRunStore _store;
    private readonly ILogger<GetEvalRunHistoryQueryHandler> _logger;

    /// <summary>Initializes a new instance.</summary>
    public GetEvalRunHistoryQueryHandler(
        IEvalRunStore store,
        ILogger<GetEvalRunHistoryQueryHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<EvalRunSummary>>> Handle(
        GetEvalRunHistoryQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var rows = await _store.GetRecentAsync(request.Take, cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyList<EvalRunSummary>>.Success(rows);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load eval run history (take={Take}).", request.Take);
            return Result<IReadOnlyList<EvalRunSummary>>.Fail(
                $"Failed to load eval run history: {ex.Message}");
        }
    }
}
