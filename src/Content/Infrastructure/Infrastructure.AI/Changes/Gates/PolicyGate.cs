using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Services.Tools;
using Domain.Common.Helpers;
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
                return GateResult.Fail(SafeFailureText.For($"Policy '{policy.Key}' threw", ex));
            }

            allFindings.AddRange(findings);
        }

        if (allFindings.Count == 0)
        {
            return GateResult.Pass($"{policies.Count} policy(ies) evaluated, no findings");
        }

        // A finding with RequiresVerification set carries a model-assigned severity that has not
        // been independently confirmed against the artifact it is about — it is excluded from the
        // severity/threshold comparison entirely (neither blocking nor "passing below threshold";
        // simply not counted), unless the SAME finding also sets Blocking, meaning a verifier already
        // confirmed it. Every existing policy leaves both flags at their false default, so this is
        // byte-identical to the prior `f.Severity >= threshold` check for every finding in the repo
        // today. See PolicyFinding.RequiresVerification's remarks.
        var blocking = allFindings.Where(f => f.Blocking || (f.Severity >= threshold && !f.RequiresVerification)).ToList();
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
        // Name-only. This is the #296 defect shape verbatim: the threshold drives
        // `f.Severity >= threshold`, so a numeric "99" parses cleanly, exceeds every real severity,
        // and the gate keeps reporting "evaluated, N findings below threshold" while blocking
        // nothing. Failing to a strict default is only meaningful if the parse can actually fail.
        if (EnumNameHelper.TryParseName<PolicyFindingSeverity>(raw, out var parsed))
        {
            return parsed;
        }

        // Default to High when the configured value is unrecognized — strict
        // default matching GovernanceConfig.InjectionBlockThreshold semantics.
        return PolicyFindingSeverity.High;
    }
}
