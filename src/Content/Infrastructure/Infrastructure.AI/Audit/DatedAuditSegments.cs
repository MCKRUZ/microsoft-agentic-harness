using System.Globalization;

namespace Infrastructure.AI.Audit;

/// <summary>
/// The files one audit chain is stored in: one file per day, named <c>yyyy-MM-dd.jsonl</c> by the UTC date it was
/// appended on, in one folder, optionally preceded by the single file the chain was written to before it was split
/// by day (#416).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why by day.</b> A chain kept in one file only ever grows, and nothing about it can be finished, moved or
/// retired. Split by day, every file but the newest is finished: no writer appends to it again, because
/// <see cref="HashChainedJsonlWriter"/> only ever appends to the latest file or starts a later one.
/// </para>
/// <para>
/// <b>The earlier single file comes first.</b> A log written before this split keeps its file, and the first
/// day-file chains onto its last record, so the history stays one chain. Its name would not sort before a date, so
/// the order is defined here, by <see cref="Compare"/>, and not by file name.
/// </para>
/// <para>
/// <b>Only day-files belong to the chain.</b> Any other file in the folder is ignored: it is neither read nor
/// verified, and the writer never appends to it. Renaming a day-file to something else removes it from the chain,
/// which verification reports as missing records.
/// </para>
/// </remarks>
public sealed class DatedAuditSegments
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string Extension = ".jsonl";

    private readonly TimeProvider _clock;

    /// <summary>Initializes the layout.</summary>
    /// <param name="folder">The folder holding the day-files.</param>
    /// <param name="legacyFile">The single file the chain was written to before it was split by day, or <c>null</c>.</param>
    /// <param name="clock">Decides which day a new record is appended to.</param>
    public DatedAuditSegments(string folder, string? legacyFile, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(clock);

        Folder = folder;
        LegacyFile = legacyFile;
        _clock = clock;
    }

    /// <summary>
    /// The layout for a chain that was written to one file: its day-files go in a folder of the same name without
    /// the extension (<c>governance.jsonl</c> is followed by <c>governance/2026-10-20.jsonl</c>), and the file
    /// itself stays first.
    /// </summary>
    /// <param name="legacyFile">The chain's single file, whether or not it exists.</param>
    /// <param name="clock">Decides which day a new record is appended to.</param>
    /// <returns>The layout.</returns>
    public static DatedAuditSegments BesideLegacyFile(string legacyFile, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyFile);
        var folder = Path.Combine(Path.GetDirectoryName(legacyFile) ?? string.Empty, Path.GetFileNameWithoutExtension(legacyFile));
        return new DatedAuditSegments(folder, legacyFile, clock);
    }

    /// <summary>Gets the folder holding the day-files.</summary>
    public string Folder { get; }

    /// <summary>Gets the single file the chain was written to before it was split by day, if any.</summary>
    public string? LegacyFile { get; }

    /// <summary>The day-file a record appended now goes to.</summary>
    /// <returns>The path of today's (UTC) day-file, whether or not it exists yet.</returns>
    public string Current() => PathFor(DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime));

    /// <summary>The day-file for <paramref name="day"/>.</summary>
    /// <param name="day">A UTC calendar day.</param>
    /// <returns>Its path, whether or not it exists.</returns>
    public string PathFor(DateOnly day) =>
        Path.Combine(Folder, day.ToString(DateFormat, CultureInfo.InvariantCulture) + Extension);

    /// <summary>Every existing file of the chain, in chain order: the earlier single file, then the day-files by date.</summary>
    /// <returns>Full paths in the order the chain runs through them.</returns>
    public IReadOnlyList<string> Ordered()
    {
        var segments = new List<string>();
        if (LegacyFile is not null && File.Exists(LegacyFile))
            segments.Add(LegacyFile);

        if (Directory.Exists(Folder))
        {
            segments.AddRange(Directory.GetFiles(Folder, "*" + Extension)
                .Where(path => DayOf(path) is not null)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal));
        }

        return segments;
    }

    /// <summary>Orders two files of this chain: the earlier single file first, then day-files by date.</summary>
    /// <param name="first">A file of this chain.</param>
    /// <param name="second">Another file of this chain.</param>
    /// <returns>Negative when <paramref name="first"/> comes first, zero when they are the same file, positive otherwise.</returns>
    public int Compare(string first, string second)
    {
        var firstIsLegacy = IsLegacy(first);
        var secondIsLegacy = IsLegacy(second);
        if (firstIsLegacy || secondIsLegacy)
            return secondIsLegacy.CompareTo(firstIsLegacy);

        return string.CompareOrdinal(Path.GetFileName(first), Path.GetFileName(second));
    }

    /// <summary>The day a day-file holds records for.</summary>
    /// <param name="path">A path.</param>
    /// <returns>The day, or <c>null</c> when the name is not exactly <c>yyyy-MM-dd.jsonl</c>.</returns>
    public static DateOnly? DayOf(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(Extension, StringComparison.Ordinal))
            return null;

        return DateOnly.TryParseExact(
            name[..^Extension.Length], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    private bool IsLegacy(string path) =>
        LegacyFile is not null && string.Equals(Path.GetFileName(path), Path.GetFileName(LegacyFile), StringComparison.Ordinal);
}
