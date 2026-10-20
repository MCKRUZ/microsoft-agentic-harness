using Domain.Common.Config;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Where each hash-chained audit log is stored: the one definition the five audit sinks write through and the audit
/// archiver reads (#416), so the two cannot disagree about which files belong to which log.
/// </summary>
/// <remarks>
/// A log's archive folder is named after its day folder (<see cref="DatedAuditSegments.ArchivedUnder"/>):
/// <c>governance</c>, <c>escalations</c>, <c>changes</c>, <c>egress</c> and <c>drift-audit</c>. Renaming one of
/// those folders moves the log away from its archive, so they are fixed here.
/// </remarks>
public static class AuditChains
{
    /// <summary>The governance decision log.</summary>
    /// <param name="config">Application configuration.</param>
    /// <param name="clock">Decides which day-file a record is appended to.</param>
    /// <returns>The log's location.</returns>
    public static AuditChainLocation Governance(AppConfig config, TimeProvider clock) =>
        BesideLegacyFile("governance", Path.Combine(config.AI.Governance.AuditStoragePath, "governance.jsonl"), clock);

    /// <summary>The escalation log.</summary>
    /// <param name="config">Application configuration.</param>
    /// <param name="clock">Decides which day-file a record is appended to.</param>
    /// <returns>The log's location.</returns>
    public static AuditChainLocation Escalations(AppConfig config, TimeProvider clock) =>
        BesideLegacyFile("escalations", Path.Combine(config.AI.Governance.Escalation.AuditStoragePath, "escalations.jsonl"), clock);

    /// <summary>The change-gate log.</summary>
    /// <param name="config">Application configuration.</param>
    /// <param name="clock">Decides which day-file a record is appended to.</param>
    /// <returns>The log's location.</returns>
    public static AuditChainLocation Changes(AppConfig config, TimeProvider clock) =>
        BesideLegacyFile("changes", Path.Combine(config.AI.Changes.AuditStoragePath, "changes.jsonl"), clock);

    /// <summary>The egress decision log.</summary>
    /// <param name="config">Application configuration.</param>
    /// <param name="clock">Decides which day-file a record is appended to.</param>
    /// <returns>The log's location.</returns>
    public static AuditChainLocation Egress(AppConfig config, TimeProvider clock) =>
        BesideLegacyFile("egress", Path.Combine(config.AI.Egress.AuditStoragePath, "egress.jsonl"), clock);

    /// <summary>The drift detection log, which was split by day from the start and has no earlier single file.</summary>
    /// <param name="config">Application configuration.</param>
    /// <param name="clock">Decides which day-file a record is appended to.</param>
    /// <returns>The log's location.</returns>
    public static AuditChainLocation Drift(AppConfig config, TimeProvider clock)
    {
        var folder = Path.Combine(config.AI.DriftDetection.AuditPath, "drift-audit");
        // Shared by every process writing these segments; not itself a *.jsonl segment.
        return new AuditChainLocation("drift", new DatedAuditSegments(folder, legacyFile: null, clock), Path.Combine(folder, ".chain.lock"));
    }

    /// <summary>Every log whose storage path is configured, in a fixed order.</summary>
    /// <param name="config">Application configuration.</param>
    /// <param name="clock">Decides which day-file a record is appended to.</param>
    /// <returns>The configured logs.</returns>
    public static IReadOnlyList<AuditChainLocation> All(AppConfig config, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);

        var chains = new List<AuditChainLocation>();
        if (!string.IsNullOrWhiteSpace(config.AI.Governance.AuditStoragePath))
            chains.Add(Governance(config, clock));
        if (!string.IsNullOrWhiteSpace(config.AI.Governance.Escalation.AuditStoragePath))
            chains.Add(Escalations(config, clock));
        if (!string.IsNullOrWhiteSpace(config.AI.Changes.AuditStoragePath))
            chains.Add(Changes(config, clock));
        if (!string.IsNullOrWhiteSpace(config.AI.Egress.AuditStoragePath))
            chains.Add(Egress(config, clock));
        if (!string.IsNullOrWhiteSpace(config.AI.DriftDetection.AuditPath))
            chains.Add(Drift(config, clock));
        return chains;
    }

    private static AuditChainLocation BesideLegacyFile(string name, string legacyFile, TimeProvider clock) =>
        new(name, DatedAuditSegments.BesideLegacyFile(legacyFile, clock), legacyFile + ".lock");
}
