using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Changes.Gates;

/// <summary>
/// Built-in gate that runs every registered <see cref="IChangeProposalPolicy"/>
/// against the proposal, aggregates findings, and maps the maximum severity
/// against <c>ChangesConfig.PolicyBlockingSeverity</c>.
/// </summary>
/// <remarks>
/// <para>
/// Policies enumerated via <c>IServiceProvider.GetServices&lt;IChangeProposalPolicy&gt;</c>
/// — no keying since the proposal does not know which policies should run against
/// it. Each policy decides whether it applies (returning an empty findings list
/// when irrelevant).
/// </para>
/// <para>
/// Aggregation: gather findings from every policy, pick the highest
/// <see cref="PolicyFindingSeverity"/>, compare to the blocking threshold.
/// At or above threshold → <see cref="GateAction.Fail"/> with the offending
/// findings serialized into <see cref="GateResult.Reason"/>. Below → Pass
/// with a count summary.
/// </para>
/// </remarks>
public sealed class PolicyGate : IChangeProposalGate
{
    private readonly IEnumerable<IChangeProposalPolicy> _policies;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<PolicyGate> _logger;

    /// <summary>Initializes a new <see cref="PolicyGate"/>.</summary>
    public PolicyGate(
        IEnumerable<IChangeProposalPolicy> policies,
        IOptionsMonitor<AppConfig> config,
        ILogger<PolicyGate> logger)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _policies = policies;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => WellKnownGateKeys.Policy;

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

        var policies = _policies.ToList();
        if (policies.Count == 0)
        {
            return GateResult.Fail(
                "No IChangeProposalPolicy is registered. " +
                "Register at least one before enabling AppConfig.AI.Changes.Enabled, or omit " +
                "the 'policy' key from the proposal's RequiredGates.");
        }

        var threshold = ParseThreshold(_config.CurrentValue.AI.Changes.PolicyBlockingSeverity);
        var allFindings = new List<PolicyFinding>();

        foreach (var policy in policies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<PolicyFinding> findings;
            try
            {
                findings = await policy.EvaluateAsync(proposal, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Policy '{PolicyKey}' threw evaluating proposal {ProposalId}.",
                    policy.Key,
                    proposal.Id);
                return GateResult.Fail($"Policy '{policy.Key}' threw: {ex.GetType().Name}: {ex.Message}");
            }

            allFindings.AddRange(findings);
        }

        if (allFindings.Count == 0)
        {
            return GateResult.Pass($"{policies.Count} policy(ies) evaluated, no findings");
        }

        var blocking = allFindings.Where(f => f.Severity >= threshold).ToList();
        if (blocking.Count > 0)
        {
            var summary = string.Join("; ", blocking.Take(5).Select(f =>
                $"[{f.Severity} {f.PolicyKey}] {f.Message}"));
            var more = blocking.Count > 5 ? $" (+{blocking.Count - 5} more)" : string.Empty;
            return GateResult.Fail(
                $"{blocking.Count} blocking finding(s) at or above {threshold}: {summary}{more}");
        }

        return GateResult.Pass(
            $"{policies.Count} policy(ies) evaluated, {allFindings.Count} finding(s) below {threshold} threshold");
    }

    private static PolicyFindingSeverity ParseThreshold(string raw)
    {
        if (Enum.TryParse<PolicyFindingSeverity>(raw, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        // Default to High when the configured value is unrecognized — strict
        // default matching GovernanceConfig.InjectionBlockThreshold semantics.
        return PolicyFindingSeverity.High;
    }
}
