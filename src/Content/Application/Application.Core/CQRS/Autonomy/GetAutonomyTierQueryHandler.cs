using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Agents;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Autonomy;

/// <summary>
/// Resolves the effective autonomy tier for a subagent type via the shared
/// <see cref="IAutonomyTierResolver"/> — the same resolver the enforcement path consults —
/// so the read surface can never drift from what enforcement actually applies.
/// </summary>
/// <remarks>
/// Read-only by construction: the handler's only dependency besides logging is the resolver,
/// which performs registry and configuration lookups. Nothing is written, audited, or cached.
/// </remarks>
public sealed class GetAutonomyTierQueryHandler
    : IRequestHandler<GetAutonomyTierQuery, Result<AutonomyTierDetail>>
{
    private readonly IAutonomyTierResolver _tierResolver;
    private readonly ILogger<GetAutonomyTierQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="GetAutonomyTierQueryHandler"/> class.</summary>
    /// <param name="tierResolver">The shared tier resolver the enforcement path also uses.</param>
    /// <param name="logger">Logger for unknown-type diagnostics (caller-supplied values are never logged verbatim).</param>
    public GetAutonomyTierQueryHandler(
        IAutonomyTierResolver tierResolver,
        ILogger<GetAutonomyTierQueryHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(tierResolver);
        ArgumentNullException.ThrowIfNull(logger);
        _tierResolver = tierResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<AutonomyTierDetail>> Handle(
        GetAutonomyTierQuery request, CancellationToken cancellationToken)
    {
        if (!AutonomyValidationRules.TryParseEnumName<SubagentType>(
                request.SubagentType, out var subagentType))
        {
            // The raw value is caller-controlled and deliberately kept out of the log line.
            _logger.LogWarning("Autonomy tier read for an unknown subagent type returned NotFound");
            return Task.FromResult(Result<AutonomyTierDetail>.NotFound(
                AutonomyValidationRules.UnknownSubagentTypeMessage));
        }

        var tier = _tierResolver.Resolve(subagentType);

        return Task.FromResult(Result<AutonomyTierDetail>.Success(
            new AutonomyTierDetail(subagentType, tier)));
    }
}
