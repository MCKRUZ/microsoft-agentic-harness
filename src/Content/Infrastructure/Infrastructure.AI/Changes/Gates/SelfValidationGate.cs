using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Changes.Gates;

/// <summary>
/// Built-in gate that runs every <see cref="IChangeProposalValidator"/> registered
/// for the proposal's <see cref="ChangeTarget.Kind"/> and aggregates their results.
/// First <see cref="GateAction.Fail"/> short-circuits; first <see cref="GateAction.Defer"/>
/// short-circuits with the longest requested retry interval propagated to the
/// orchestrator.
/// </summary>
/// <remarks>
/// <para>
/// Validators are enumerated via
/// <c>IServiceProvider.GetKeyedServices&lt;IChangeProposalValidator&gt;(targetKind)</c>.
/// Empty enumeration → <see cref="GateAction.Fail"/> with a directive message
/// pointing at DI registration; never silently passes an unvalidated proposal.
/// </para>
/// </remarks>
public sealed class SelfValidationGate : IChangeProposalGate
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SelfValidationGate> _logger;

    /// <summary>Initializes a new <see cref="SelfValidationGate"/>.</summary>
    public SelfValidationGate(IServiceProvider services, ILogger<SelfValidationGate> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => WellKnownGateKeys.SelfValidation;

    /// <inheritdoc />
    public GatePhase Phase => GatePhase.Validation;

    /// <inheritdoc />
    public async Task<GateResult> EvaluateAsync(
        ChangeProposal proposal,
        GateContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);

        var validators = _services
            .GetKeyedServices<IChangeProposalValidator>(proposal.Target.Kind)
            .ToList();

        if (validators.Count == 0)
        {
            return GateResult.Fail(
                $"No IChangeProposalValidator registered for target kind '{proposal.Target.Kind}'. " +
                "Register one keyed by the ChangeTargetKind before enabling AppConfig.AI.Changes.Enabled.");
        }

        TimeSpan? longestDefer = null;
        var deferReasons = new List<string>();
        var passReasons = new List<string>();

        foreach (var validator in validators)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GateResult result;
            try
            {
                result = await validator.ValidateAsync(proposal, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Validator '{ValidatorKey}' threw evaluating proposal {ProposalId}.",
                    validator.Key,
                    proposal.Id);
                return GateResult.Fail($"Validator '{validator.Key}' threw: {ex.GetType().Name}: {ex.Message}");
            }

            switch (result.Action)
            {
                case GateAction.Fail:
                    return GateResult.Fail(
                        $"Validator '{validator.Key}' failed: {result.Reason}",
                        result.EvidenceHash);

                case GateAction.Defer:
                    deferReasons.Add($"{validator.Key}: {result.Reason}");
                    if (result.RetryAfter is { } retry && (longestDefer is null || retry > longestDefer))
                    {
                        longestDefer = retry;
                    }
                    break;

                case GateAction.Pass:
                    if (!string.IsNullOrEmpty(result.Reason))
                    {
                        passReasons.Add($"{validator.Key}: {result.Reason}");
                    }
                    break;
            }
        }

        if (longestDefer is { } interval)
        {
            return GateResult.Defer(
                $"{deferReasons.Count} validator(s) deferred: {string.Join("; ", deferReasons)}",
                interval);
        }

        var summary = passReasons.Count > 0
            ? string.Join("; ", passReasons)
            : $"{validators.Count} validator(s) passed";
        return GateResult.Pass(summary);
    }
}
