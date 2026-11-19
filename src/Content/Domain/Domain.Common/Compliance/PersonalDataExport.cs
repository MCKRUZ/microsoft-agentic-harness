namespace Domain.Common.Compliance;

/// <summary>
/// A machine-readable copy of everything the harness can find for one person (#902): the right of access (GDPR Art. 15)
/// and of portability (Art. 20).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The manifest says what the copy leaves out.</strong> Every store the host runs that can hold the person's data
/// has a line: what it returned, or why it returned nothing. <see cref="IsComplete"/> is <see langword="true"/> only when
/// no line leaves data out (<see cref="ExportManifestEntry.LeavesDataOut"/>), so a copy never reads as everything while a
/// store failed or could not be searched.
/// </para>
/// <para>
/// Made on request and returned at once, never stored: a copy kept for later download would be one more place the
/// person's data lives, needing its own retention, erasure and access control.
/// </para>
/// </remarks>
/// <param name="RequestId">Identifies this request in the audit trail.</param>
/// <param name="GeneratedAt">When the copy was made.</param>
/// <param name="Manifest">One line per store.</param>
/// <param name="Sections">What each store that returned data returned.</param>
public sealed record PersonalDataExport(
    string RequestId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ExportManifestEntry> Manifest,
    IReadOnlyList<ExportSection> Sections)
{
    /// <summary>Whether every store that can hold the person's data returned it.</summary>
    public bool IsComplete => Manifest.All(m => !m.LeavesDataOut);

    /// <summary>
    /// The host process this copy covers, such as <c>Presentation.AgentHub</c> or <c>Presentation.ExecutionApi</c>
    /// (#1003). A copy holds only what the process that made it can find, so <see cref="IsComplete"/> speaks for that host
    /// alone: a deployment that runs several hosts holding personal data sends the request to each. Set by the request path
    /// that hands the copy out; <see langword="null"/> on a copy taken straight from the export services.
    /// </summary>
    public string? Host { get; init; }
}
