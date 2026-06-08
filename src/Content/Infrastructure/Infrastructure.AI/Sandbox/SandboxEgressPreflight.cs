using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Egress;
using Domain.AI.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Default <see cref="ISandboxEgressPreflight"/> implementation. Resolves the
/// active per-skill <see cref="IEgressPolicy"/> from the ambient agent
/// identity and runs every URI in
/// <c>SandboxExecutionRequest.EgressPrecheckTargets</c> through it before the
/// process or container is spawned.
/// </summary>
/// <remarks>
/// <para>
/// Each decision is written to the egress JSONL audit so dashboards can
/// distinguish "sandbox preflight" hits from runtime allowlist hits — both
/// land in the same audit. The digest is a stable SHA-256 over a canonical
/// encoding of the decisions so the verifier can reconstruct it from the
/// audit and confirm an attestation matches.
/// </para>
/// </remarks>
public sealed class SandboxEgressPreflight : ISandboxEgressPreflight
{
    private readonly IServiceProvider _rootServices;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly IEgressPolicyResolver _resolver;
    private readonly IEgressAuditWriter _auditWriter;
    private readonly ILogger<SandboxEgressPreflight> _logger;
    private readonly TimeProvider _timeProvider;

    private static readonly AgentIdentity UnattributedIdentity = new()
    {
        Id = "sandbox.unattributed",
        Kind = AgentIdentityKind.Unspecified
    };

    /// <summary>Initializes a new <see cref="SandboxEgressPreflight"/>.</summary>
    public SandboxEgressPreflight(
        IServiceProvider rootServices,
        IAmbientRequestScope ambientScope,
        IEgressPolicyResolver resolver,
        IEgressAuditWriter auditWriter,
        ILogger<SandboxEgressPreflight> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(rootServices);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _rootServices = rootServices;
        _ambientScope = ambientScope;
        _resolver = resolver;
        _auditWriter = auditWriter;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EgressDecision>> EvaluateAsync(
        IReadOnlyList<Uri> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
        {
            return [];
        }

        var identity = ResolveIdentity();
        var (effectiveIdentity, policy) = (identity, identity is not null ? _resolver.ResolveFor(identity) : null);

        var decisions = new List<EgressDecision>(targets.Count);
        foreach (var target in targets)
        {
            EgressDecision decision;
            if (effectiveIdentity is null || policy is null)
            {
                decision = new EgressDecision
                {
                    Allowed = false,
                    Reason = "Sandbox preflight blocked: no agent identity bound to the request scope.",
                    Target = target,
                    DecidedAt = _timeProvider.GetUtcNow()
                };
            }
            else
            {
                decision = await policy.AllowAsync(target, effectiveIdentity, cancellationToken).ConfigureAwait(false);
            }

            await _auditWriter.AppendAsync(decision, effectiveIdentity ?? UnattributedIdentity, cancellationToken).ConfigureAwait(false);

            if (!decision.Allowed)
            {
                _logger.LogWarning(
                    "Sandbox preflight denied egress to '{Host}' for identity '{Identity}': {Reason}",
                    target.Host,
                    effectiveIdentity?.Id ?? UnattributedIdentity.Id,
                    decision.Reason);
            }

            decisions.Add(decision);
        }

        return decisions;
    }

    /// <inheritdoc />
    public string ComputeDigest(IReadOnlyList<EgressDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        if (decisions.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var d in decisions)
        {
            // Canonical encoding: target|allowed|matched-entry — does not
            // include timestamp so the digest is deterministic across replays
            // of the same decision sequence.
            sb.Append(d.Target.AbsoluteUri);
            sb.Append('|');
            sb.Append(d.Allowed ? '1' : '0');
            sb.Append('|');
            sb.Append(d.MatchedAllowlistEntry ?? "none");
            sb.Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    private AgentIdentity? ResolveIdentity()
    {
        var requestServices = _ambientScope.Current ?? _rootServices;
        var context = requestServices.GetService<IAgentExecutionContext>();
        return context?.AgentIdentity;
    }
}
