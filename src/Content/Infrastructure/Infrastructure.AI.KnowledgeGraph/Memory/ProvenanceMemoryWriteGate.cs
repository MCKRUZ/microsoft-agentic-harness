using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.Governance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph.Memory;

/// <summary>
/// Default <see cref="IMemoryWriteGate"/>: scans candidate facts for prompt injection, optionally
/// runs an intent ("Task Adherence") check, stamps conversation-memory provenance, and audits the
/// decision — all before a fact is persisted. Implements the "establish intent and provenance
/// before persistence" principle from Microsoft's <em>Guarding AI memory</em> guidance.
/// </summary>
/// <remarks>
/// <para>
/// Classification ladder, per <c>MemoryGuardConfig</c>:
/// <list type="bullet">
///   <item><description>Injection at or above the reject threshold → not persisted (only the rejection is audited).</description></item>
///   <item><description>Injection at or above the quarantine threshold → persisted but marked <see cref="MemoryTrust.Untrusted"/> (withheld from recall).</description></item>
///   <item><description>Otherwise, when the intent check is enabled and reports misalignment → quarantined.</description></item>
///   <item><description>Otherwise → persisted as <see cref="MemoryTrust.Trusted"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// The deterministic injection scan is the always-on protection; the intent check is an opt-in seam.
/// No candidate content is ever written to logs or the audit chain — only the key, entity type, and
/// classification — so the audit trail cannot leak sensitive or attacker-controlled text.
/// </para>
/// </remarks>
public sealed class ProvenanceMemoryWriteGate : IMemoryWriteGate
{
    private const string AuditAgent = "knowledge_memory";
    private const string MemoryPipeline = "conversation_memory";
    private const string MemoryTask = "fact_extraction";

    private readonly IPromptInjectionScanner? _scanner;
    private readonly IMemoryIntentClassifier _intentClassifier;
    private readonly IProvenanceStamper _provenanceStamper;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly IGovernanceAuditService? _audit;
    private readonly ILogger<ProvenanceMemoryWriteGate> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProvenanceMemoryWriteGate"/> class.
    /// </summary>
    /// <param name="provenanceStamper">Stamps conversation-memory provenance on persisted facts.</param>
    /// <param name="intentClassifier">The intent ("Task Adherence") check; a fail-open no-op by default.</param>
    /// <param name="config">Application configuration (memory guard thresholds, provenance toggle).</param>
    /// <param name="logger">Logger for gate decisions and degraded-mode warnings.</param>
    /// <param name="scanner">Deterministic prompt-injection scanner. When absent, the gate cannot
    /// classify injection and treats content as trusted (degraded mode), logged as a warning.</param>
    /// <param name="audit">Tamper-evident governance audit chain. Falls back to structured logging when absent.</param>
    public ProvenanceMemoryWriteGate(
        IProvenanceStamper provenanceStamper,
        IMemoryIntentClassifier intentClassifier,
        IOptionsMonitor<AppConfig> config,
        ILogger<ProvenanceMemoryWriteGate> logger,
        IPromptInjectionScanner? scanner = null,
        IGovernanceAuditService? audit = null)
    {
        ArgumentNullException.ThrowIfNull(provenanceStamper);
        ArgumentNullException.ThrowIfNull(intentClassifier);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _provenanceStamper = provenanceStamper;
        _intentClassifier = intentClassifier;
        _config = config;
        _logger = logger;
        _scanner = scanner;
        _audit = audit;

        if (scanner is null)
        {
            _logger.LogWarning(
                "Memory write gate constructed without a prompt-injection scanner; injection " +
                "classification is disabled and facts will be treated as trusted. Wire " +
                "IPromptInjectionScanner (register the governance layer) to enable full protection.");
        }
    }

    /// <inheritdoc />
    public async Task<MemoryWriteDecision> EvaluateAsync(
        string key,
        string content,
        string entityType,
        CancellationToken cancellationToken = default)
    {
        // Snapshot config once so the guard thresholds and provenance toggle below are read from a
        // single consistent view, even if config reloads mid-evaluate.
        var config = _config.CurrentValue.AI;
        var guard = config.KnowledgeBridge.MemoryGuard;
        if (!guard.Enabled)
            return MemoryWriteDecision.Allow();

        var scan = _scanner?.Scan(content ?? string.Empty) ?? InjectionScanResult.Clean();

        // Reject is the more severe action and must never sit below the quarantine bar — otherwise a
        // misconfiguration (RejectThreshold < QuarantineThreshold) would silently drop facts that
        // should be quarantine-and-retained for forensics. Clamp defensively to honor the documented
        // contract regardless of config order.
        var rejectThreshold = guard.RejectThreshold < guard.QuarantineThreshold
            ? guard.QuarantineThreshold
            : guard.RejectThreshold;

        // Reject: never persist content that trips the reject threshold; audit only the decision.
        if (scan.IsInjection && scan.ThreatLevel >= rejectThreshold)
        {
            var rejectReason = $"rejected: {scan.ThreatLevel}/{scan.InjectionType}";
            Audit(key, entityType, persist: false, rejectReason);
            return new MemoryWriteDecision
            {
                Persist = false,
                Trust = MemoryTrust.Untrusted,
                Reason = rejectReason
            };
        }

        var quarantine = scan.IsInjection && scan.ThreatLevel >= guard.QuarantineThreshold;
        var classification = quarantine ? $"quarantined: injection/{scan.InjectionType}" : "trusted";

        // Optional intent ("Task Adherence") check — only when not already quarantined.
        if (!quarantine && guard.IntentCheckEnabled)
        {
            var intent = await _intentClassifier
                .ClassifyAsync(content ?? string.Empty, entityType, cancellationToken)
                .ConfigureAwait(false);

            if (!intent.Aligned)
            {
                quarantine = true;
                classification = $"quarantined: intent/{intent.Reason ?? "misaligned"}";
            }
        }

        var trust = quarantine ? MemoryTrust.Untrusted : MemoryTrust.Trusted;
        var provenance = config.Rag.GraphRag.ProvenanceEnabled
            ? _provenanceStamper.CreateStamp(MemoryPipeline, MemoryTask)
            : null;

        Audit(key, entityType, persist: true, classification);

        return new MemoryWriteDecision
        {
            Persist = true,
            Trust = trust,
            Provenance = provenance,
            Reason = classification
        };
    }

    private void Audit(string key, string entityType, bool persist, string classification)
    {
        var decision = persist ? $"persist/{classification}" : classification;

        if (_audit is not null)
            _audit.Log(AuditAgent, $"memory_write:{entityType}:{key}", decision);
        else
            _logger.LogInformation(
                "Memory write {Decision} for key {Key} (type {EntityType})", decision, key, entityType);
    }
}
