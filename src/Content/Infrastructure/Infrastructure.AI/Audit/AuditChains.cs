using Domain.Common.Config;
using Domain.Common.Config.AI;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Where each hash-chained audit log is stored: the one definition the six audit sinks write through and the audit
/// archiver reads (#416), so the two cannot disagree about which files belong to which log.
/// </summary>
/// <remarks>
/// <para>
/// A log's archive folder is named after its day folder (<see cref="DatedAuditSegments.ArchivedUnder"/>):
/// <c>governance</c>, <c>escalations</c>, <c>changes</c>, <c>egress</c>, <c>drift-audit</c> and <c>compliance</c>. Renaming one of
/// those folders moves the log away from its archive, so they are fixed here.
/// </para>
/// <para>
/// Each log's held records wait beside its day folder, in the same name with <c>-pending</c> added (#1001):
/// <c>governance-pending</c>, <c>escalations-pending</c>, <c>changes-pending</c>, <c>egress-pending</c>,
/// <c>drift-audit-pending</c> and <c>compliance-pending</c>. Inside the log's configured folder, so every rule that keeps
/// that folder apart (from the archive, from another log) keeps the held records apart too.
/// </para>
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
        return new AuditChainLocation(
            "drift", new DatedAuditSegments(folder, legacyFile: null, clock), Path.Combine(folder, ".chain.lock"), PendingBeside(folder));
    }

    /// <summary>
    /// The compliance log (#969): every erasure, data export and document removal. Split by day from the start, like the
    /// drift log, so it has no earlier single file.
    /// </summary>
    /// <param name="config">Application configuration.</param>
    /// <param name="clock">Decides which day-file a record is appended to.</param>
    /// <returns>The log's location.</returns>
    public static AuditChainLocation Compliance(AppConfig config, TimeProvider clock)
    {
        // A fixed folder name inside the configured path, so the archive folder never takes its name from configuration.
        var folder = Path.Combine(config.AI.Erasure.AuditStoragePath, ErasureConfig.TrailFolderName);
        return new AuditChainLocation(
            "compliance",
            new DatedAuditSegments(folder, legacyFile: null, clock),
            Path.Combine(folder, ".chain.lock"),
            Path.Combine(config.AI.Erasure.AuditStoragePath, ErasureConfig.PendingFolderName));
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
        if (!string.IsNullOrWhiteSpace(config.AI.Erasure.AuditStoragePath))
            chains.Add(Compliance(config, clock));
        return chains;
    }

    private static AuditChainLocation BesideLegacyFile(string name, string legacyFile, TimeProvider clock)
    {
        var segments = DatedAuditSegments.BesideLegacyFile(legacyFile, clock);
        return new(name, segments, legacyFile + ".lock", PendingBeside(segments.Folder));
    }

    // A sibling of the day folder, never inside it: a file in the day folder that is not a day-file is ignored by the
    // chain, but the archiver and the overlap checks treat that folder as the log's own.
    private static string PendingBeside(string dayFolder) =>
        Path.TrimEndingDirectorySeparator(dayFolder) + "-pending";
}
