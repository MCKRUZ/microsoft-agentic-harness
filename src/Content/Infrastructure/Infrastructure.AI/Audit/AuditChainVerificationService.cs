using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
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
public sealed class AuditChainVerificationService : BackgroundService
{
    private static readonly JsonSerializerOptions ReceiptOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IReadOnlyList<IVerifiableAuditChain> _chains;
    private readonly IOptionsMonitor<AppConfig> _config;
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
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The smallest interval allowed between passes, to prevent a misconfigured hot loop.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _config.CurrentValue.AI.Audit;
        var initialDelay = settings.InitialDelay > TimeSpan.Zero ? settings.InitialDelay : TimeSpan.Zero;
        var interval = settings.VerificationInterval >= MinimumInterval
            ? settings.VerificationInterval
            : MinimumInterval;

        if (settings.VerificationInterval < MinimumInterval)
            _logger.LogWarning(
                "Audit verification interval {Configured} is below the {Minimum} floor; using the floor.",
                settings.VerificationInterval, MinimumInterval);

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
        var receiptPath = _config.CurrentValue.AI.Audit.ReceiptPath;

        foreach (var chain in _chains)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await chain.VerifyChainAsync(cancellationToken).ConfigureAwait(false);

                var nameTag = new KeyValuePair<string, object?>(
                    GovernanceConventions.AuditChainNameTag, chain.AuditName);
                GovernanceMetrics.AuditChainVerifications.Add(1, nameTag);

                if (result.IsValid)
                {
                    _logger.LogInformation(
                        "Audit chain '{AuditName}' verified intact: {VerifiedCount} record(s).",
                        chain.AuditName, result.VerifiedCount);
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

            Directory.CreateDirectory(receiptPath);
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
