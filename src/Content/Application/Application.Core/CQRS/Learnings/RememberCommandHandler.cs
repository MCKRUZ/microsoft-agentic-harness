using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Learnings;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Captures a new learning by persisting it to the store and emitting a notification.
/// Maps <see cref="LearningCategory"/> to default <see cref="Domain.AI.Learnings.DecayClass"/>
/// when no explicit decay class is provided on the command.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the learnings channel's write chokepoint, and the memory write gate belongs
/// here (issue #338).</strong> A learning is model- or conversation-derived text that
/// <c>LearningsRecallContextProvider</c> later replays verbatim into the <em>instruction</em> channel
/// of a future turn — the same loop the knowledge-memory gate was built to close. Both writers of
/// note (drift-escalation resolutions and the work-memory synthesis pass) reach the store through
/// this handler, so gating here covers the channel; gating at either caller would not.
/// </para>
/// <para>
/// Only the gate's <c>Persist</c> and <c>Trust</c> verdicts are applied here. Its graph-provenance
/// stamp is not: a learning already carries richer, caller-supplied <see cref="LearningProvenance"/>
/// (origin pipeline, task, timestamp, confidence), and the gate audits its own decision internally,
/// so nothing is lost by leaving the graph stamp to the graph channel.
/// </para>
/// <para>
/// The gate is a <em>required</em> dependency rather than an optional one. An optional gate makes
/// "nobody registered it" indistinguishable from "it allowed the write", which is precisely the
/// silently-ungated shape this handler used to be. It is registered alongside
/// <see cref="ILearningsStore"/> in the same composition, so a host that can construct this handler
/// can always supply it.
/// </para>
/// </remarks>
public sealed class RememberCommandHandler : IRequestHandler<RememberCommand, Result<LearningEntry>>
{
    /// <summary>
    /// Stable, scrubbed failure code returned when the gate refuses a write. The gate's own reason
    /// string is deliberately <em>not</em> surfaced: it names the injection classification, and this
    /// result travels to callers that log it.
    /// </summary>
    internal const string RejectedErrorCode = "learnings.write_rejected";

    /// <summary>
    /// Entity type reported to the gate. The gate's optional intent check asks "does this content
    /// match its claimed entity type", so naming the channel — not the learning's category — is what
    /// makes that question answerable.
    /// </summary>
    private const string GateEntityType = "Learning";

    private readonly ILearningsStore _store;
    private readonly ILearningNotificationChannel _notifications;
    private readonly IMemoryWriteGate _writeGate;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RememberCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="RememberCommandHandler"/> class.</summary>
    /// <param name="store">Durable learnings persistence.</param>
    /// <param name="notifications">Channel notified once a learning is captured.</param>
    /// <param name="writeGate">Scans candidate content for injection and classifies its trust before
    /// it is persisted. Required — see the remarks on <see cref="RememberCommandHandler"/>.</param>
    /// <param name="options">Application configuration; supplies the learnings enablement flag.</param>
    /// <param name="timeProvider">Clock used for the entry's timestamps.</param>
    /// <param name="logger">Logger for gate decisions and notification failures.</param>
    public RememberCommandHandler(
        ILearningsStore store,
        ILearningNotificationChannel notifications,
        IMemoryWriteGate writeGate,
        IOptionsMonitor<AppConfig> options,
        TimeProvider timeProvider,
        ILogger<RememberCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(writeGate);

        _store = store;
        _notifications = notifications;
        _writeGate = writeGate;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<LearningEntry>> Handle(RememberCommand request, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.AI.Learnings.Enabled)
        {
            _logger.LogDebug("Learnings subsystem disabled, skipping remember");
            return Result<LearningEntry>.Success(CreatePlaceholder(request));
        }

        var decayClass = request.DecayClass ?? MapCategoryToDecayClass(request.Category);
        var now = _timeProvider.GetUtcNow();
        var learningId = Guid.NewGuid();

        // Gate before anything is persisted: scan for injection and classify trust. Rejected content
        // is never written; quarantined content is written but withheld from recall, so it stays
        // available for audit and incident response without ever reaching an agent's instructions.
        var decision = await _writeGate.EvaluateAsync(
            learningId.ToString(), request.Content, GateEntityType, cancellationToken);

        if (!decision.Persist)
        {
            _logger.LogWarning(
                "Learning write blocked for {LearningId} from {SourceType}: {Reason}",
                learningId, request.Source.SourceType, decision.Reason);

            return Result<LearningEntry>.Fail(RejectedErrorCode);
        }

        if (decision.Trust == MemoryTrust.Untrusted)
        {
            _logger.LogWarning(
                "Learning {LearningId} from {SourceType} quarantined; it is retained for audit but "
                + "will not be recalled into agent context: {Reason}",
                learningId, request.Source.SourceType, decision.Reason);
        }

        var entry = new LearningEntry
        {
            LearningId = learningId,
            Category = request.Category,
            DecayClass = decayClass,
            Scope = request.Scope,
            Content = request.Content,
            Source = request.Source,
            Provenance = request.Provenance,
            Trust = decision.Trust,
            FeedbackWeight = 1.0,
            UpdateCount = 0,
            CreatedAt = now,
            LastAccessedAt = now,
            LastReinforcedAt = now
        };

        var saveResult = await _store.SaveAsync(entry, cancellationToken);
        if (!saveResult.IsSuccess)
            return Result<LearningEntry>.Fail(saveResult.Errors.ToArray());

        try
        {
            await _notifications.NotifyLearningCapturedAsync(entry, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify learning captured for {LearningId}", entry.LearningId);
        }

        LearningsMetrics.Remembered.Add(1,
            new KeyValuePair<string, object?>(LearningConventions.Category, request.Category.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>(LearningConventions.Scope, GetScopeTag(request.Scope)));

        return Result<LearningEntry>.Success(entry);
    }

    internal static DecayClass MapCategoryToDecayClass(LearningCategory category) => category switch
    {
        LearningCategory.FactualCorrection => DecayClass.Permanent,
        LearningCategory.DomainKnowledge => DecayClass.Permanent,
        LearningCategory.InstructionUpdate => DecayClass.Stable,
        LearningCategory.StylePreference => DecayClass.Stable,
        LearningCategory.ToolUsagePattern => DecayClass.Stable,
        _ => DecayClass.Stable
    };

    private static string GetScopeTag(LearningScope scope) =>
        scope.AgentId is not null ? LearningConventions.ScopeValues.Agent :
        scope.TeamId is not null ? LearningConventions.ScopeValues.Team :
        LearningConventions.ScopeValues.Global;

    private LearningEntry CreatePlaceholder(RememberCommand request) => new()
    {
        LearningId = Guid.Empty,
        Category = request.Category,
        DecayClass = request.DecayClass ?? MapCategoryToDecayClass(request.Category),
        Scope = request.Scope,
        Content = request.Content,
        Source = request.Source,
        Provenance = request.Provenance,
        CreatedAt = _timeProvider.GetUtcNow()
    };
}
