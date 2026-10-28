using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Audit;
using Application.Core.Compliance;
using Domain.AI.Audit;
using Domain.Common;
using Domain.Common.Compliance;
using Domain.Common.Config;
using Domain.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Audit;

/// <summary>
/// The shipped <see cref="IComplianceAuditTrail"/> (#969): one hash-chained JSONL record per erasure, data export and
/// document removal step, in day-files under <c>{Erasure:AuditStoragePath}/compliance</c>, through the same
/// <see cref="HashChainedJsonlWriter"/> as the other five audit trails.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it lasts.</strong> Nothing deletes these files. The audit archiver moves finished day-files to the archive
/// with the other trails when <c>AppConfig:AI:Audit:ArchivePath</c> is set, scheduled verification walks the chain, and
/// erasure lists the trail on every receipt as kept by obligation instead of erasing from it, which would break the chain
/// that makes it evidence.
/// </para>
/// <para>
/// <strong>What a record holds.</strong> The <see cref="AuditEntry"/> as written, snake_case, with one change: a named
/// operator's id is replaced by its <see cref="IAuditPseudonymizer.Person"/> reference, as the other trails record
/// approvers and reviewers (#903). The key holder can still find every request an operator made; the operator's own id is
/// not kept for good. A person erasing their own data, the document age limit and an unidentified operator are recorded by
/// their fixed names, which identify no one.
/// </para>
/// <para>
/// <strong>A record that cannot be appended is held, not dropped (#995).</strong> When the chain is busy, the disk is
/// full or the folder cannot be written, the record is held in <c>{Erasure:AuditStoragePath}/compliance-pending</c> and
/// <see cref="ReplayPendingAsync"/> writes it to the chain later. See <c>JsonlComplianceAuditTrail.Pending.cs</c>.
/// </para>
/// </remarks>
[PersonalDataStore(
    "Hash-chained JSONL day-files under {Erasure:AuditStoragePath}/compliance (default .agent-sessions/compliance) of every erasure, data export and document removal: the requesting operator as a keyed reference, the subject as a keyed reference when Erasure:SubjectReferenceKey is set, the receipt id, completeness and per-store counts. Kept by design: deleting records would break the chain and the proof it holds. A record that could not be appended is held, as the same line, in {Erasure:AuditStoragePath}/compliance-pending until replay writes it to the chain, then deleted.",
    PersonalDataCategories.Identifiers,
    PersonalDataKey.Unkeyed,
    RetentionBasis.KeptByDesign,
    ErasureSupport.Exempt,
    ExportSupport.NotSupported)]
public sealed partial class JsonlComplianceAuditTrail : IComplianceAuditTrail, IVerifiableAuditChain, IDisposable
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _folder;
    private readonly HashChainedJsonlWriter _chain;
    private readonly IAuditPseudonymizer _pseudonymizer;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="JsonlComplianceAuditTrail"/> class.</summary>
    /// <param name="config">Application configuration providing the trail's folder and the archive.</param>
    /// <param name="timeProvider">Decides which day-file a record is appended to.</param>
    /// <param name="pseudonymizer">Records a named operator as a keyed reference.</param>
    /// <param name="logger">Logger the chain writer reports its failures through.</param>
    public JsonlComplianceAuditTrail(
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        IAuditPseudonymizer pseudonymizer,
        ILogger<JsonlComplianceAuditTrail> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(pseudonymizer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.CurrentValue.AI.Erasure.AuditStoragePath, "Erasure.AuditStoragePath");

        var chain = AuditChains.Compliance(config.CurrentValue, timeProvider);
        _folder = chain.Segments.Folder;
        _chain = new HashChainedJsonlWriter(chain, logger, config.CurrentValue.AI.Audit.ArchivePath);
        _pseudonymizer = pseudonymizer;
        _clock = timeProvider;
        _logger = logger;
        _pendingFolder = PendingFolder(config.CurrentValue);
    }

    /// <inheritdoc />
    public string AuditName => "compliance";

    /// <inheritdoc />
    public string AuditLocation => _folder;

    /// <inheritdoc />
    /// <remarks>
    /// A record the chain refuses, or whose append throws, is held for replay and still returned as a failure that says
    /// so: it is not in the trail yet. Only when it cannot be held either is it lost, logged as critical.
    /// </remarks>
    public async Task<Result> RecordAsync(AuditEntry entry, ErasureActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(actor);

        // Serialized once, after the operator is replaced by their reference: a held record is this exact line, so the
        // pending folder never holds the operator's id and replay never pseudonymises a reference twice.
        var payload = JsonSerializer.Serialize(entry with { ExecutorId = RecordedExecutor(actor) }, SerializeOptions);
        Result appended;
        try
        {
            // The writer logs its own failures; the caller logs the returned reason once more, with its request.
            appended = await _chain.AppendAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // For example the writer disposed while the host shuts down, or the caller cancelling: the request this
            // records has already happened, so the record is held all the same.
            _logger.LogError(MessageWithheldException.From(ex),
                "Compliance record not written to {AuditLocation}: the append threw", _folder);
            appended = Result.Fail($"The append threw {ex.GetType().Name}.");
        }

        return appended.IsSuccess ? appended : Hold(entry.Action, payload, appended.Errors);
    }

    /// <inheritdoc />
    public Task<AuditChainVerificationResult> VerifyChainAsync(
        AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken) =>
        _chain.VerifyChainAsync(checkpoint, cancellationToken);

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose() => _chain.Dispose();

    // Only a named operator's id is a person's own id; the other actors are recorded under fixed names. Decided by the
    // actor's kind, not its id, so an operator whose id reads like a fixed name is still recorded as a reference.
    private string RecordedExecutor(ErasureActor actor) =>
        actor.IsNamedOperator
            ? _pseudonymizer.Person(actor.ExecutorId)
            : actor.ExecutorId;
}
