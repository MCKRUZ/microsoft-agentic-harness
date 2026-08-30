using Application.AI.Common.Interfaces.Verification;
using Domain.AI.Verification;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Verification;

/// <summary>
/// Dispatches one <see cref="IObligationVerifier"/> call per obligation, bounded by
/// <see cref="Domain.Common.Config.AI.ObligationConfig.MaxObligations"/> and
/// <see cref="Domain.Common.Config.AI.ObligationConfig.MaxParallelVerifiers"/>.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on <c>EvalRunner.RunParallelAsync</c>'s flat fan-out (a <see cref="SemaphoreSlim"/> gate,
/// <c>Select</c>, <c>WhenAll</c>) rather than <c>PlanExecutor</c>: obligations have no dependencies on
/// each other, so a DAG scheduler's ready-queue, checkpointing, and recovery ladder would all be
/// inert here. Unlike <c>EvalRunner</c>'s own per-case try/catch (which already lives inside
/// <c>ScoreCaseAsync</c>), the try/catch here is this type's whole reason to exist: every exception a
/// verifier can throw — including a per-verifier timeout — is converted to
/// <see cref="VerificationVerdict.VerifierError"/> INSIDE the fan-out lambda, so nothing ever reaches
/// <c>WhenAll</c> and one bad obligation cannot take down the other N-1 verdicts.
/// </para>
/// <para>
/// This is also the chokepoint <see cref="ObligationValidator"/>'s own remarks refer to as "the
/// only place any obligation, from any extractor, is checked before use" — rejection happens here,
/// not inside a specific <see cref="Application.AI.Common.Interfaces.Verification.IObligationExtractor"/>
/// implementation, so a future second extractor gets the same guarantee without having to
/// remember to call the validator itself.
/// </para>
/// </remarks>
public sealed class ObligationVerificationRunner
{
    private readonly IObligationVerifier _verifier;
    private readonly ObligationValidator _validator;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ObligationVerificationRunner> _logger;

    /// <summary>Initializes a new instance of the <see cref="ObligationVerificationRunner"/> class.</summary>
    public ObligationVerificationRunner(
        IObligationVerifier verifier,
        ObligationValidator validator,
        IOptionsMonitor<AppConfig> config,
        ILogger<ObligationVerificationRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _verifier = verifier;
        _validator = validator;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Verifies every obligation in <paramref name="obligations"/> that passes <see cref="ObligationValidator"/>
    /// (up to <see cref="Domain.Common.Config.AI.ObligationConfig.MaxObligations"/>) concurrently,
    /// bounded by <see cref="Domain.Common.Config.AI.ObligationConfig.MaxParallelVerifiers"/>, against
    /// <paramref name="artifactContent"/> — the same artifact every obligation in
    /// <paramref name="obligations"/> was extracted from. A rejected obligation produces no verdict
    /// at all — it is not dispatched, and does not appear in the returned list. Never throws for a
    /// per-obligation verification failure — see this type's remarks.
    /// </summary>
    public async Task<IReadOnlyList<VerificationVerdict>> RunAsync(
        IReadOnlyList<Obligation> obligations, string artifactContent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(obligations);
        ArgumentNullException.ThrowIfNull(artifactContent);

        var config = _config.CurrentValue.AI.Obligations;

        // ObligationConfigValidator + ValidateOnStart guards these at startup, but IOptionsMonitor
        // can pick up a hot-reloaded config value that never goes through that startup check again.
        // A MaxParallelVerifiers of 0 hangs every verifier on gate.WaitAsync forever (no timeout can
        // rescue a permit that's never granted); a negative value throws out of SemaphoreSlim's own
        // constructor; a non-positive PerVerifierTimeout throws out of CancelAfter — all three would
        // escape RunAsync uncaught, breaking the "never throws" contract this type documents. Clamped
        // here rather than trusted, the same way ObligationValidator re-checks obligations that were
        // already validated upstream.
        var maxParallelVerifiers = config.MaxParallelVerifiers > 0 ? config.MaxParallelVerifiers : 1;
        var perVerifierTimeout = config.PerVerifierTimeout > TimeSpan.Zero
            ? config.PerVerifierTimeout
            : TimeSpan.FromSeconds(30);
        if (maxParallelVerifiers != config.MaxParallelVerifiers || perVerifierTimeout != config.PerVerifierTimeout)
        {
            _logger.LogWarning(
                "ObligationConfig has an invalid MaxParallelVerifiers ({MaxParallelVerifiers}) or " +
                "PerVerifierTimeout ({PerVerifierTimeout}) — a hot-reloaded value bypassing startup " +
                "validation. Falling back to {ClampedMaxParallelVerifiers}/{ClampedPerVerifierTimeout}.",
                config.MaxParallelVerifiers, config.PerVerifierTimeout, maxParallelVerifiers, perVerifierTimeout);
        }

        var validated = obligations.Where(o => _validator.Validate(o).IsValid).ToList();
        if (validated.Count < obligations.Count)
        {
            _logger.LogInformation(
                "Rejected {Rejected} of {Total} extracted obligations as malformed — they were not dispatched to a verifier.",
                obligations.Count - validated.Count, obligations.Count);
        }

        var bounded = validated.Count > config.MaxObligations
            ? validated.Take(config.MaxObligations).ToList()
            : validated;

        if (bounded.Count < validated.Count)
        {
            _logger.LogWarning(
                "Extraction produced {Total} valid obligations, exceeding MaxObligations {Max} — " +
                "{Dropped} obligation(s) were NOT verified.",
                validated.Count, config.MaxObligations, validated.Count - bounded.Count);
        }

        using var gate = new SemaphoreSlim(maxParallelVerifiers);
        var tasks = bounded.Select(async obligation =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            // Linked, not a bare WaitAsync race: passing timeoutCts.Token (not cancellationToken)
            // into VerifyAsync means a per-verifier timeout actually cancels the verifier's own
            // in-flight work — its chat-client call included — instead of merely racing it while
            // the abandoned call keeps running in the background and still occupies a concurrency
            // slot in spirit even after this method has released its semaphore permit.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(perVerifierTimeout);
            try
            {
                return await _verifier.VerifyAsync(obligation, artifactContent, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The `when` filter already guarantees that if ex is an OperationCanceledException,
                // cancellationToken (the outer, whole-run token) was NOT the source — so within this
                // catch, an OperationCanceledException can only mean timeoutCts's own CancelAfter fired.
                // An OperationCanceledException that DOES match cancellationToken is let through: the
                // whole run was cancelled, not just this one obligation, and there is nothing fail-safe
                // to report.
                var timedOut = ex is OperationCanceledException;
                _logger.LogWarning(ex,
                    "Obligation verifier failed for the obligation relying on '{ReliesOn}' — reporting " +
                    "VerifierError (fail-safe) rather than propagating.", obligation.ReliesOn);
                // Full exception detail is logged above; never echoed into the returned reason —
                // VerificationVerdict.Explanation is persisted (surfaced via MetricScore.Reasoning),
                // and an unfiltered exception message has leaked sensitive detail elsewhere in this repo.
                var reason = timedOut
                    ? $"Verifier exceeded the {perVerifierTimeout} timeout."
                    : "Obligation verifier failed; see logs for details.";
                return VerificationVerdict.VerifierError(obligation, reason);
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
