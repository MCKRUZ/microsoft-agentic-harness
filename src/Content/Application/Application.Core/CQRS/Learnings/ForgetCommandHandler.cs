using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Soft-deletes a learning entry from the store with an audit reason.
/// </summary>
public sealed class ForgetCommandHandler : IRequestHandler<ForgetCommand, Result>
{
    private readonly ILearningsStore _store;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<ForgetCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="ForgetCommandHandler"/> class.</summary>
    public ForgetCommandHandler(
        ILearningsStore store,
        IOptionsMonitor<AppConfig> options,
        ILogger<ForgetCommandHandler> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> Handle(ForgetCommand request, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.AI.Learnings.Enabled)
        {
            _logger.LogDebug("Learnings subsystem disabled, skipping forget");
            return Result.Success();
        }

        var result = await _store.SoftDeleteAsync(request.LearningId, request.Reason, cancellationToken);
        if (!result.IsSuccess)
            return result;

        LearningsMetrics.Forgotten.Add(1);
        return Result.Success();
    }
}
