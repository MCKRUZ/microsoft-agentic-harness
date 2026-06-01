using Application.AI.Common.Evaluation.Interfaces;
using Domain.AI.Evaluation;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.Evaluation.GetEvalRunDetail;

/// <summary>
/// Handles <see cref="GetEvalRunDetailQuery"/> by delegating to
/// <see cref="IEvalRunStore.GetRunDetailAsync"/>. Translates null result to
/// <see cref="Result{T}.NotFound"/>.
/// </summary>
public sealed class GetEvalRunDetailQueryHandler
    : IRequestHandler<GetEvalRunDetailQuery, Result<EvalRunReport>>
{
    private readonly IEvalRunStore _store;
    private readonly ILogger<GetEvalRunDetailQueryHandler> _logger;

    /// <summary>Initializes a new instance.</summary>
    public GetEvalRunDetailQueryHandler(
        IEvalRunStore store,
        ILogger<GetEvalRunDetailQueryHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<EvalRunReport>> Handle(
        GetEvalRunDetailQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        EvalRunReport? report;
        try
        {
            report = await _store.GetRunDetailAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load eval run {RunId}.", request.RunId);
            return Result<EvalRunReport>.Fail($"Failed to load eval run: {ex.Message}");
        }

        return report is null
            ? Result<EvalRunReport>.NotFound($"Eval run '{request.RunId}' not found.")
            : Result<EvalRunReport>.Success(report);
    }
}
