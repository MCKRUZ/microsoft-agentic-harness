namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the memory write gate — the defense that scans, classifies, and stamps
/// provenance on facts before they enter long-term memory, and quarantines untrusted facts from
/// recall. Bound to <c>AppConfig:AI:KnowledgeBridge:MemoryGuard</c>.
/// </summary>
/// <remarks>
/// Implements Microsoft's <em>Guarding AI memory</em> principles for the harness's cross-session
/// memory: "establish intent and provenance before persistence" and "treat retrieval as a risk
/// decision." The gate closes the unattended auto-extraction write path
/// (<c>KnowledgeExtractionBehavior</c> → <c>RememberAsync</c>), which otherwise bypasses the
/// request pipeline's content-safety and prompt-injection behaviors.
/// <para>
/// Coverage boundary: the gate protects the <c>IKnowledgeMemory</c> path (the auto-extraction
/// memory used by the agent). The separate <c>ICrossSessionMemoryStore</c> subsystem is not routed
/// through this gate; secure it independently if a deployment feeds it back into agent context.
/// </para>
/// </remarks>
public sealed class MemoryGuardConfig
{
    /// <summary>
    /// Whether the memory write gate is active. <strong>Defaults to <see langword="true"/></strong>:
    /// the parent <see cref="KnowledgeBridgeConfig.Enabled"/> already gates memory as a whole, so once
    /// a consumer deliberately turns memory on, the protection is on by default (defense-by-default).
    /// When false, writes pass through unguarded and unclassified, preserving legacy behavior.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The minimum prompt-injection <see cref="ThreatLevel"/> at which a fact is persisted but
    /// quarantined from recall (marked <c>Untrusted</c>). Defaults to <see cref="ThreatLevel.Medium"/>.
    /// </summary>
    public ThreatLevel QuarantineThreshold { get; set; } = ThreatLevel.Medium;

    /// <summary>
    /// The minimum prompt-injection <see cref="ThreatLevel"/> at which a fact is rejected outright —
    /// not written at all, only the rejection audited. Defaults to <see cref="ThreatLevel.Critical"/>.
    /// Must be at least <see cref="QuarantineThreshold"/>.
    /// </summary>
    public ThreatLevel RejectThreshold { get; set; } = ThreatLevel.Critical;

    /// <summary>
    /// Whether to run the optional <c>IMemoryIntentClassifier</c> ("Task Adherence") check on each
    /// candidate fact. Defaults to <see langword="false"/> — the seam ships with a fail-open no-op
    /// default, so this stays off until a consumer plugs in a real (typically LLM-backed) classifier.
    /// </summary>
    public bool IntentCheckEnabled { get; set; } = false;
}
