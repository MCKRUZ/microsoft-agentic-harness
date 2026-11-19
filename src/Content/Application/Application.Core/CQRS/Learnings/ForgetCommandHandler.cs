using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Soft-deletes a learning entry from the store with an audit reason.
/// </summary>
/// <remarks>
/// A learning scoped to one user (<see cref="Domain.AI.Learnings.LearningScope.UserId"/>, #968) can
/// be forgotten only by that user. Any other caller, including one with no knowledge scope, gets
/// not-found, the same answer as for a learning that does not exist, so the id reveals nothing.
/// </remarks>
public sealed class ForgetCommandHandler : IRequestHandler<ForgetCommand, Result>
{
    private readonly ILearningsStore _store;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<ForgetCommandHandler> _logger;
    private readonly IKnowledgeScope? _knowledgeScope;

    /// <summary>Initializes a new instance of the <see cref="ForgetCommandHandler"/> class.</summary>
    /// <param name="store">Durable learnings persistence.</param>
    /// <param name="options">Application configuration; supplies the learnings enablement flag.</param>
    /// <param name="logger">Logger for the disabled short-circuit.</param>
    /// <param name="knowledgeScope">The caller's knowledge scope. Null means the caller is unknown,
    /// so no user-scoped learning can be forgotten.</param>
    public ForgetCommandHandler(
        ILearningsStore store,
        IOptionsMonitor<AppConfig> options,
        ILogger<ForgetCommandHandler> logger,
        IKnowledgeScope? knowledgeScope = null)
    {
        _store = store;
        _options = options;
        _logger = logger;
        _knowledgeScope = knowledgeScope;
    }

    /// <inheritdoc />
    public async Task<Result> Handle(ForgetCommand request, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.AI.Learnings.Enabled)
        {
            _logger.LogDebug("Learnings subsystem disabled, skipping forget");
            return Result.Success();
        }

        var getResult = await _store.GetAsync(request.LearningId, cancellationToken);
        if (!getResult.IsSuccess || getResult.Value is null
            || !getResult.Value.Scope.IsVisibleTo(ScopeIdentity.Canonicalize(_knowledgeScope?.UserId)))
            return Result.NotFound($"Learning {request.LearningId} not found");

        var result = await _store.SoftDeleteAsync(request.LearningId, request.Reason, cancellationToken);
        if (!result.IsSuccess)
            return result;

        LearningsMetrics.Forgotten.Add(1);
        return Result.Success();
    }
}
