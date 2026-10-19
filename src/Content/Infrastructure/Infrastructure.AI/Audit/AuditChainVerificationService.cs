using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.Common.Helpers;
using Domain.AI.Audit;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Domain.Common.Config.AI.Audit;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Background service that periodically verifies the integrity of every hash-chained audit log.
/// A clean pass confirms no record has been edited, deleted, or reordered since it was written;
/// a broken pass raises a critical alert (log + metric) and records a receipt. This is the job
/// that turns the cryptographic hash-chain into an operational guarantee — without it, tampering
/// is only ever caught opportunistically on read.
/// </summary>
/// <remarks>
/// Each clean pass records a checkpoint (the newest record's sequence and hash) and the next pass
/// requires the chain to still reach it unchanged (#416). Without that, a log whose newest records
/// were cut off, or one deleted and regrown after a restart, verifies as intact: what remains is
/// internally consistent. The head is also written to the structured log, so a copy leaves the host
/// when log export is on. A broken pass does not advance the checkpoint, so the alert repeats every
/// pass until someone looks.
/// </remarks>
public sealed class AuditChainVerificationService : BackgroundService
{
    private static readonly JsonSerializerOptions ReceiptOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IReadOnlyList<IVerifiableAuditChain> _chains;
    private readonly LastKnownGoodSetting<AuditConfig> _audit;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditChainVerificationService> _logger;

    /// <summary>Initializes a new <see cref="AuditChainVerificationService"/>.</summary>
    /// <param name="chains">All verifiable audit chains registered in the host.</param>
    /// <param name="config">Application configuration providing the audit schedule and receipt path.</param>
    /// <param name="timeProvider">Time provider for scheduling and receipt timestamps.</param>
    /// <param name="logger">Logger for verification results and operational diagnostics.</param>
    public AuditChainVerificationService(
        IEnumerable<IVerifiableAuditChain> chains,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<AuditChainVerificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(chains);
        _chains = chains.ToList();
        _timeProvider = timeProvider;
        _logger = logger;

        // Read at the start of ExecuteAsync and again per pass; a throw from a hot-reloaded value that failed
        // validation must not stop the host (#821). Seeded with the shipped defaults: verification only reads
        // the chains and writes a receipt, so the default schedule is the safe fallback.
        _audit = new LastKnownGoodSetting<AuditConfig>(
            () => config.CurrentValue.AI.Audit, new AuditConfig(), logger, "AppConfig:AI:Audit");
    }

    /// <summary>The smallest interval allowed between passes, to prevent a misconfigured hot loop.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = _audit.Current.InitialDelay > TimeSpan.Zero ? _audit.Current.InitialDelay : TimeSpan.Zero;
        TimeSpan? warnedBelowFloor = null;

        try
        {
            await Task.Delay(initialDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Re-read each pass, so a value that could not be read at start (and fell back to the shipped
            // schedule) is picked up once the configuration is readable again.
            var configured = _audit.Current.VerificationInterval;
            var interval = configured >= MinimumInterval ? configured : MinimumInterval;
            if (configured < MinimumInterval && warnedBelowFloor != configured)
            {
                warnedBelowFloor = configured;
                _logger.LogWarning(
                    "Audit verification interval {Configured} is below the {Minimum} floor; using the floor.",
                    configured, MinimumInterval);
            }

            // Survive any non-cancellation fault: an audit-integrity verifier must not take the
            // host down (the default BackgroundService behavior is StopHost) on a transient error.
            try
            {
                await VerifyAllChainsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit chain verification pass failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs a single verification pass over all registered chains, emitting metrics, logs, and a
    /// receipt per chain. Exposed for testing the pass without the scheduling loop. A failure in
    /// one chain never aborts verification of the others.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task VerifyAllChainsAsync(CancellationToken cancellationToken)
    {
        var receiptPath = _audit.Current.ReceiptPath;
        var checkpoints = string.IsNullOrWhiteSpace(receiptPath)
            ? null
            : new AuditChainCheckpointStore(receiptPath, _logger);

        foreach (var chain in _chains)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var checkpoint = checkpoints is null
                    ? null
                    : await checkpoints.ReadAsync(chain.AuditName, cancellationToken).ConfigureAwait(false);
                var result = await chain.VerifyChainAsync(checkpoint, cancellationToken).ConfigureAwait(false);

                var nameTag = new KeyValuePair<string, object?>(
                    GovernanceConventions.AuditChainNameTag, chain.AuditName);
                GovernanceMetrics.AuditChainVerifications.Add(1, nameTag);

                if (result.IsValid)
                {
                    _logger.LogInformation(
                        "Audit chain '{AuditName}' verified intact: {VerifiedCount} record(s), head sequence {HeadSequence} hash {HeadHash}.",
                        chain.AuditName, result.VerifiedCount, result.HeadSequence, result.HeadHash);

                    if (checkpoints is not null && result is { HeadSequence: >= 0, HeadHash: { } headHash })
                    {
                        await checkpoints.WriteAsync(
                            chain.AuditName, new AuditChainCheckpoint(result.HeadSequence, headHash), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    GovernanceMetrics.AuditChainBreaks.Add(1, nameTag);
                    _logger.LogCritical(
                        "Audit chain '{AuditName}' INTEGRITY BROKEN at sequence {BrokenSequence}: {FailureReason} ({VerifiedCount} record(s) verified before the break).",
                        chain.AuditName, result.FirstBrokenSequence, result.FailureReason, result.VerifiedCount);
                }

                await WriteReceiptAsync(receiptPath, chain.AuditName, result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to verify audit chain '{AuditName}'.", chain.AuditName);
            }
        }
    }

    private async Task WriteReceiptAsync(
        string receiptPath, string auditName, Domain.AI.Audit.AuditChainVerificationResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(receiptPath))
            return;

        try
        {
            var now = _timeProvider.GetUtcNow();
            var receipt = new VerificationReceipt
            {
                VerifiedAt = now,
                AuditName = auditName,
                IsValid = result.IsValid,
                VerifiedCount = result.VerifiedCount,
                FirstBrokenSequence = result.FirstBrokenSequence,
                FailureReason = result.FailureReason
            };

            // Owner-only (#660, following #640/#527's precedent): a receipt reports which audit
            // chains verified clean or broken, including a failure reason -- confidentiality-
            // relevant on a shared host, not just tamper-evident.
            OwnerOnlyDirectoryHelper.Create(receiptPath, _logger);
            var file = Path.Combine(receiptPath, $"{now:yyyy-MM-dd}.jsonl");
            var line = JsonSerializer.Serialize(receipt, ReceiptOptions) + "\n";

            await using var stream = new FileStream(
                file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Receipt persistence is best-effort: a bad ReceiptPath (invalid chars, too long) or any
            // IO failure must degrade to a warning, never escalate — the result is already logged and metered.
            _logger.LogWarning(ex, "Failed to write audit verification receipt for '{AuditName}'.", auditName);
        }
    }

    private sealed record VerificationReceipt
    {
        public required DateTimeOffset VerifiedAt { get; init; }
        public required string AuditName { get; init; }
        public required bool IsValid { get; init; }
        public required long VerifiedCount { get; init; }
        public long? FirstBrokenSequence { get; init; }
        public string? FailureReason { get; init; }
    }
}
