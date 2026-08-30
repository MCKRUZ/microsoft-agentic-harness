using Application.AI.Common.Interfaces.ClaimVerification;
using Domain.AI.ClaimVerification;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.ClaimVerification;

/// <summary>
/// Dispatches one <see cref="IClaimVerifier"/> call per high-consequence claim, resolving each
/// claim's evidence via the <see cref="ILocatedArtifactReader"/> keyed to its location scheme.
/// Bounded by <see cref="ClaimVerificationConfig.MaxParallelVerifiers"/>.
/// </summary>
/// <remarks>
/// <para>
/// Modeled on <c>ObligationVerificationRunner</c>'s flat fan-out (a <see cref="SemaphoreSlim"/>
/// gate, <c>Select</c>, <c>WhenAll</c>, per-item try/catch inside the fan-out lambda so nothing
/// reaches <c>WhenAll</c> unconverted) — the two runners solve the same shaped problem
/// (independent, unbounded-cost verifications with no dependencies on each other) and share the
/// same reasoning for rejecting a DAG scheduler. They are deliberately NOT the same type: an
/// obligation's evidence is a slice of an already-fetched shared artifact; a claim's evidence must
/// be independently resolved per claim, via whichever reader its own <see cref="Claim.Location"/>
/// scheme selects — see <c>IClaimVerifier</c>'s remarks.
/// </para>
/// <para>
/// Consequence classification happens here, not in <see cref="IClaimVerifier"/> or a reader — it is
/// this runner's own gate on whether a reader or verifier is ever invoked at all, exactly as
/// <c>ObligationValidator</c> is <c>ObligationVerificationRunner</c>'s own gate on whether a
/// verifier is invoked.
/// </para>
/// <para>
/// Registered unconditionally in every host (unlike <c>ObligationVerificationRunner</c>, which is
/// gated behind the eval-only <c>AddObligationVerification()</c>): this type has no direct
/// dependency on a judge model, only on the interfaces it is handed, and its first caller
/// (<c>TrainSkillCommandHandler</c>) runs in every host, not just an eval host. A host that has not
/// opted into real verification simply gets <c>NotConfiguredClaimVerifier</c>'s fail-safe
/// <see cref="ClaimVerificationOutcome.Unverifiable"/> for every claim.
/// </para>
/// </remarks>
public sealed class ClaimVerificationRunner
{
    private readonly IClaimConsequenceClassifier _classifier;
    private readonly IClaimVerifier _verifier;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ClaimVerificationRunner> _logger;

    // Derived from ClaimVerificationConfig's own property-initializer defaults rather than
    // re-literaled here, for the same reason ObligationVerificationRunner.Defaults is — a
    // hand-typed second copy would silently drift the next time someone changes the config type
    // without remembering this fallback.
    private static readonly ClaimVerificationConfig Defaults = new();

    /// <summary>Initializes a new instance of the <see cref="ClaimVerificationRunner"/> class.</summary>
    public ClaimVerificationRunner(
        IClaimConsequenceClassifier classifier,
        IClaimVerifier verifier,
        IServiceProvider serviceProvider,
        IOptionsMonitor<AppConfig> config,
        ILogger<ClaimVerificationRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _classifier = classifier;
        _verifier = verifier;
        _serviceProvider = serviceProvider;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Verifies every claim in <paramref name="claims"/> (up to
    /// <see cref="ClaimVerificationConfig.MaxClaims"/>) concurrently, bounded by
    /// <see cref="ClaimVerificationConfig.MaxParallelVerifiers"/>. A claim beyond the cap produces no
    /// verdict at all — it is not dispatched, and does not appear in the returned list, so a caller
    /// that needs 1:1 correspondence with its input must treat a shorter result as "the trailing
    /// claims were never checked," not as an error. A low-consequence claim (per
    /// <see cref="IClaimConsequenceClassifier"/>) is reported <see cref="ClaimVerificationOutcome.NotConsequential"/>
    /// without invoking any reader or verifier. Never throws for a per-claim failure — every
    /// exception a reader or verifier can raise, including a per-verifier timeout, is converted to
    /// <see cref="ClaimVerdict.VerifierError"/> inside the fan-out.
    /// </summary>
    public async Task<IReadOnlyList<ClaimVerdict>> RunAsync(
        IReadOnlyList<Claim> claims, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var (maxClaims, maxParallelVerifiers, perVerifierTimeout) = ResolveEffectiveConfig();

        var bounded = claims.Count > maxClaims ? claims.Take(maxClaims).ToList() : claims;
        if (bounded.Count < claims.Count)
        {
            _logger.LogWarning(
                "Claim batch of {Total} exceeds MaxClaims {Max} — {Dropped} claim(s) were NOT verified.",
                claims.Count, maxClaims, claims.Count - bounded.Count);
        }

        using var gate = new SemaphoreSlim(maxParallelVerifiers);
        var tasks = bounded.Select(c => VerifyOneAsync(c, gate, perVerifierTimeout, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private (int MaxClaims, int MaxParallelVerifiers, TimeSpan PerVerifierTimeout) ResolveEffectiveConfig()
    {
        var config = _config.CurrentValue.AI.ClaimVerification;

        var maxClaims = config.MaxClaims > 0 ? config.MaxClaims : Defaults.MaxClaims;
        var maxParallelVerifiers = config.MaxParallelVerifiers > 0 ? config.MaxParallelVerifiers : Defaults.MaxParallelVerifiers;
        var perVerifierTimeout = config.PerVerifierTimeout > TimeSpan.Zero
            ? config.PerVerifierTimeout
            : Defaults.PerVerifierTimeout;

        if (maxClaims != config.MaxClaims
            || maxParallelVerifiers != config.MaxParallelVerifiers
            || perVerifierTimeout != config.PerVerifierTimeout)
        {
            _logger.LogWarning(
                "ClaimVerificationConfig has an invalid MaxClaims ({MaxClaims}), MaxParallelVerifiers " +
                "({MaxParallelVerifiers}), or PerVerifierTimeout ({PerVerifierTimeout}) — a hot-reloaded " +
                "value bypassing startup validation. Falling back to " +
                "{ClampedMaxClaims}/{ClampedMaxParallelVerifiers}/{ClampedPerVerifierTimeout}.",
                config.MaxClaims, config.MaxParallelVerifiers, config.PerVerifierTimeout,
                maxClaims, maxParallelVerifiers, perVerifierTimeout);
        }

        return (maxClaims, maxParallelVerifiers, perVerifierTimeout);
    }

    private async Task<ClaimVerdict> VerifyOneAsync(
        Claim claim, SemaphoreSlim gate, TimeSpan perVerifierTimeout, CancellationToken cancellationToken)
    {
        if (_classifier.Classify(claim.ConsequenceSignals) == ClaimConsequence.Low)
        {
            return ClaimVerdict.NotConsequential(claim);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Linked, not a bare WaitAsync race — see ObligationVerificationRunner.VerifyOneAsync's
        // identical comment: passing timeoutCts.Token means a per-claim timeout actually cancels
        // the in-flight reader/verifier call, not merely races it in the background.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(perVerifierTimeout);
        try
        {
            return await ResolveAndVerifyAsync(claim, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Same filter reasoning as ObligationVerificationRunner.VerifyOneAsync: an
            // OperationCanceledException reaching this catch can only be timeoutCts's own
            // CancelAfter — the whole-run cancellation case is let through uncaught above.
            var timedOut = ex is OperationCanceledException;
            _logger.LogWarning(ex,
                "Claim verification failed for the claim at '{Location}' — reporting VerifierError " +
                "(fail-safe) rather than propagating.", claim.Location);
            var reason = timedOut
                ? $"Verification exceeded the {perVerifierTimeout} timeout."
                : "Claim verification failed; see logs for details.";
            return ClaimVerdict.VerifierError(claim, reason);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ClaimVerdict> ResolveAndVerifyAsync(Claim claim, CancellationToken cancellationToken)
    {
        var scheme = ExtractScheme(claim.Location);
        if (scheme is null)
        {
            return ClaimVerdict.Unverifiable(claim, $"Location '{claim.Location}' has no recognizable scheme.");
        }

        var reader = _serviceProvider.GetKeyedService<ILocatedArtifactReader>(scheme);
        if (reader is null)
        {
            return ClaimVerdict.Unverifiable(claim, $"No reader is registered for location scheme '{scheme}'.");
        }

        var evidence = await reader.TryReadAsync(claim.Location, cancellationToken).ConfigureAwait(false);
        if (evidence is null)
        {
            return ClaimVerdict.LocationNotFound(claim, $"Location '{claim.Location}' does not exist.");
        }

        return await _verifier.VerifyAsync(claim, evidence, cancellationToken).ConfigureAwait(false);
    }

    private static string? ExtractScheme(string location)
    {
        var separatorIndex = location.IndexOf(':');
        return separatorIndex > 0 ? location[..separatorIndex] : null;
    }
}
