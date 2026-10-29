using System.Globalization;
using Domain.AI.Audit;

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
/// verified, and the writer never appends to it. Renaming a day-file to something else removes it from the chain:
/// verification reports the missing records when the file was not the newest, or when the last checkpoint covers
/// them; the newest records since the last check are lost silently, as with any cut from the end of a chain.
/// </para>
/// <para>
/// <b>A day name in the wrong letter case stops the chain.</b> On a file system that ignores case, a planted
/// <c>2026-10-21.JSONL</c> is the same file as <c>2026-10-21.jsonl</c> to the writer but is not a day-file to
/// verification, so a whole day could be written where no check reads it. <see cref="Ordered"/> therefore refuses a
/// folder holding such a name, on every platform: appends fail and verification reports the chain broken until
/// someone removes or renames the file.
/// </para>
/// <para>
/// <b>A day-file dated after tomorrow stops the chain too.</b> The writer never appends to a file earlier than the
/// latest one, so a planted <c>2099-12-31.jsonl</c> would take every later record and, being always the newest file,
/// never be finished or archived. <see cref="Ordered"/> refuses a folder holding a day-file dated more than one day
/// after today (UTC); one day allows for a host whose clock runs a little ahead.
/// </para>
/// </remarks>
public sealed class DatedAuditSegments
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string Extension = ".jsonl";

    /// <summary>
    /// Lists every file the writer could open: names in any letter case on every platform, so a mis-cased day name is
    /// seen everywhere, and hidden or system files too, which the defaults skip although the writer can still append
    /// to them. An unreadable folder fails rather than listing as empty.
    /// </summary>
    private static readonly EnumerationOptions AnyCase = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = false
    };

    private readonly TimeProvider _clock;

    /// <summary>Decides which day a new record is appended to; also names the files a held record is kept in (#1001).</summary>
    internal TimeProvider Clock => _clock;

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
        if (!Path.HasExtension(legacyFile))
            throw new ArgumentException("The single file needs an extension, so its day-file folder has a different path.", nameof(legacyFile));

        var folder = Path.Combine(Path.GetDirectoryName(legacyFile) ?? string.Empty, Path.GetFileNameWithoutExtension(legacyFile));
        return new DatedAuditSegments(folder, legacyFile, clock);
    }

    /// <summary>
    /// The same chain's files as archived under <paramref name="archiveRoot"/>, laid out as they are live: a folder
    /// named like this one's day folder (<see cref="AuditChains"/> lists the names) holding the archived day-files,
    /// and, once archived, the earlier single file beside it under its own name.
    /// </summary>
    /// <param name="archiveRoot">The archive folder shared by every audit log (<c>AppConfig:AI:Audit:ArchivePath</c>).</param>
    /// <returns>The archive's layout for this chain. Its <see cref="Current"/> is never used.</returns>
    public DatedAuditSegments ArchivedUnder(string archiveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
        var folder = Path.Combine(archiveRoot, Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)));
        var legacy = LegacyFile is null ? null : Path.Combine(archiveRoot, Path.GetFileName(LegacyFile));
        return new DatedAuditSegments(folder, legacy, _clock);
    }

    /// <summary>Gets the folder holding the day-files.</summary>
    public string Folder { get; }

    /// <summary>Gets the single file the chain was written to before it was split by day, if any.</summary>
    public string? LegacyFile { get; }

    /// <summary>The day-file a record appended now goes to.</summary>
    /// <returns>The path of today's (UTC) day-file, whether or not it exists yet.</returns>
    public string Current() =>
        Path.Combine(Folder, DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).ToString(DateFormat, CultureInfo.InvariantCulture) + Extension);

    /// <summary>Every existing file of the chain, in chain order: the earlier single file, then the day-files by date.</summary>
    /// <returns>Full paths in the order the chain runs through them.</returns>
    /// <exception cref="AuditFolderRefusedException">
    /// The folder holds a day name in the wrong letter case, or a day-file dated after tomorrow (see the class remarks).
    /// </exception>
    public IReadOnlyList<string> Ordered()
    {
        var segments = new List<string>();
        if (LegacyFile is not null && File.Exists(LegacyFile))
            segments.Add(LegacyFile);

        if (!Directory.Exists(Folder))
            return segments;

        var latestAllowed = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).AddDays(1);
        var days = new List<string>();
        foreach (var path in Directory.GetFiles(Folder, "*" + Extension, AnyCase))
        {
            if (DayOf(path) is { } day)
            {
                if (day > latestAllowed)
                {
                    throw new AuditFolderRefusedException(
                        $"Audit folder {Folder} holds {Path.GetFileName(path)}, dated after tomorrow. Every new record would " +
                        "go to it and it would never be finished, so the chain is not written or verified until it is " +
                        "dealt with: if it holds records, a host's clock was ahead (or this host's clock is behind); " +
                        "otherwise it was placed there.");
                }

                days.Add(path);
            }
            else if (IsMiscasedDay(Path.GetFileName(path)))
                throw new AuditFolderRefusedException(
                    $"Audit folder {Folder} holds {Path.GetFileName(path)}, a day-file name in the wrong letter case. " +
                    "The chain is not written or verified until it is renamed to lower case (yyyy-MM-dd.jsonl); " +
                    "remove it only if it is empty, since it may hold audit records.");
        }

        segments.AddRange(days.OrderBy(Path.GetFileName, StringComparer.Ordinal));
        return segments;
    }

    /// <summary>
    /// Orders two files of this chain, live or archived, by name: the earlier single file first, then day-files by date.
    /// </summary>
    /// <param name="first">A file of this chain, or its archived copy.</param>
    /// <param name="second">Another.</param>
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

    private static bool IsMiscasedDay(string name) =>
        name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
        && DateOnly.TryParseExact(name[..^Extension.Length], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>
    /// The earlier single file, live or archived: its name, outside a day folder. Archived and live layouts are alike
    /// (<see cref="ArchivedUnder"/>), so this recognises either copy, while a file of that name inside the day folder is
    /// not it.
    /// </summary>
    private bool IsLegacy(string path) =>
        LegacyFile is not null
        && string.Equals(Path.GetFileName(path), Path.GetFileName(LegacyFile), StringComparison.Ordinal)
        && !string.Equals(
            Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder))),
            StringComparison.Ordinal);
}
