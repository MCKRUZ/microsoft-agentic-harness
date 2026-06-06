using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.Changes.RunGate;

/// <summary>
/// Handles <see cref="RunGateCommand"/>: load the proposal, resolve the keyed gate,
/// build the <see cref="GateContext"/>, evaluate, and return the gate's verdict.
/// </summary>
public sealed class RunGateCommandHandler
    : IRequestHandler<RunGateCommand, Result<GateResult>>
{
    private readonly IChangeProposalStore _store;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<RunGateCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="RunGateCommandHandler"/>.</summary>
    public RunGateCommandHandler(
        IChangeProposalStore store,
        IServiceProvider services,
        TimeProvider time,
        ILogger<RunGateCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _services = services;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<GateResult>> Handle(
        RunGateCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var proposal = await _store.GetAsync(request.ProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null)
        {
            return Result<GateResult>.NotFound(
                $"ChangeProposal '{request.ProposalId}' not found.");
        }

        var gate = _services.GetKeyedService<IChangeProposalGate>(request.GateKey);
        if (gate is null)
        {
            return Result<GateResult>.Fail(
                $"No IChangeProposalGate registered for key '{request.GateKey}'.");
        }

        var context = new GateContext
        {
            Mode = request.Mode,
            AttemptCount = request.AttemptCount,
            EvaluatedAt = _time.GetUtcNow(),
            CorrelationId = request.CorrelationId
        };

        try
        {
            var result = await gate.EvaluateAsync(proposal, context, cancellationToken).ConfigureAwait(false);
            return Result<GateResult>.Success(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Gate '{GateKey}' threw evaluating proposal {ProposalId} (attempt {AttemptCount}, correlation {CorrelationId}).",
                request.GateKey,
                request.ProposalId,
                request.AttemptCount,
                request.CorrelationId);
            return Result<GateResult>.Fail(
                $"Gate '{request.GateKey}' threw during evaluation: {ex.GetType().Name}");
        }
    }
}
