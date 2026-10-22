using System.Text.Json;

namespace Application.Common.Logging;

/// <summary>
/// The run folders the file log providers write under <c>Logging:LogsBasePath</c>: how a run and its phase are named, the
/// marker that proves a folder is a log run, the files a run holds, and which runs this process is writing (#901).
/// </summary>
/// <remarks>
/// <para>
/// The marker exists for the retention sweep. The logs folder is also a root the file-system tool may write into, so a
/// folder that merely holds a <c>log.txt</c> is no proof the providers wrote it; one whose marker names the folder is.
/// Runs written before the marker existed have none and are never swept.
/// </para>
/// <para>
/// A run id and a phase are each one plain folder name. Before this they were checked only by a prefix comparison that a
/// sibling folder passed (<c>../logsX</c> resolves outside <c>logs</c> yet starts with its path).
/// </para>
/// <para>
/// A run is live from <see cref="BeginRun"/> until <see cref="EndRun"/>, and the sweep deletes only through
/// <see cref="DeleteUnlessLive"/>, which holds the same lock: a provider starting a run, even under an id last used long
/// ago, either registers first and the sweep skips it, or waits until the sweep has finished and recreates its folders.
/// It covers this process only; a run another process writes into the same folder is protected by its age alone.
/// </para>
/// </remarks>
public static class LogRunFolder
{
    /// <summary>The marker at a run's root, naming the run.</summary>
    public const string MarkerFileName = "log-run.json";

    /// <summary>The free-form log written by <see cref="FileLoggerProvider"/>.</summary>
    public const string LogFileName = "log.txt";

    /// <summary>The console rendering written by <see cref="FileLoggerProvider"/>.</summary>
    public const string ConsoleFileName = "console.txt";

    /// <summary>The JSONL log written by <see cref="StructuredJsonLoggerProvider"/>.</summary>
    public const string StructuredFileName = "structured.jsonl";

    /// <summary>The run manifest <see cref="FileLoggerProvider.CompleteRun"/> writes.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The largest marker read; anything bigger was not written by a provider.</summary>
    private const long MaxMarkerBytes = 4096;

    private static readonly string[] OwnFileNames = [LogFileName, ConsoleFileName, StructuredFileName];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly object LiveGate = new();
    private static readonly Dictionary<string, int> LiveRuns = new(PathComparer);

    /// <summary>
    /// Resolves a run's folder, and its phase folder when there is one, refusing any name that is not a single plain folder
    /// name inside <paramref name="basePath"/>.
    /// </summary>
    /// <returns>The run root and the folder the run writes into.</returns>
    /// <exception cref="ArgumentException">The run id or phase is not one plain folder name.</exception>
    public static (string RunRoot, string WriteFolder) Resolve(string basePath, string runId, string? phase)
    {
        RequireFolderName(runId, nameof(runId));
        if (phase is not null)
            RequireFolderName(phase, nameof(phase));

        var runRoot = Path.Combine(basePath, runId);
        return (runRoot, phase is null ? runRoot : Path.Combine(runRoot, phase));
    }

    /// <summary>Records that this process is writing the run at <paramref name="runRoot"/>, before any file is opened.</summary>
    public static void BeginRun(string runRoot)
    {
        var key = Path.GetFullPath(runRoot);
        lock (LiveGate)
            LiveRuns[key] = LiveRuns.GetValueOrDefault(key) + 1;
    }

    /// <summary>Records that a writer of the run at <paramref name="runRoot"/> has closed its files.</summary>
    public static void EndRun(string runRoot)
    {
        var key = Path.GetFullPath(runRoot);
        lock (LiveGate)
        {
            if (LiveRuns.TryGetValue(key, out var count) && count > 1)
                LiveRuns[key] = count - 1;
            else
                LiveRuns.Remove(key);
        }
    }

    /// <summary>Whether a provider in this process is writing the run at <paramref name="runRoot"/>.</summary>
    public static bool IsLive(string runRoot)
    {
        var key = Path.GetFullPath(runRoot);
        lock (LiveGate)
            return LiveRuns.ContainsKey(key);
    }

    /// <summary>
    /// Runs <paramref name="delete"/> for the run at <paramref name="runRoot"/> unless a provider in this process is
    /// writing it, holding the lock <see cref="BeginRun"/> takes, so no run can start in the folder meanwhile.
    /// </summary>
    /// <returns>What <paramref name="delete"/> returned, or <see langword="false"/> for a live run.</returns>
    public static bool DeleteUnlessLive(string runRoot, Func<bool> delete)
    {
        ArgumentNullException.ThrowIfNull(delete);
        var key = Path.GetFullPath(runRoot);
        lock (LiveGate)
            return !LiveRuns.ContainsKey(key) && delete();
    }

    /// <summary>
    /// Writes the marker naming <paramref name="runId"/> at <paramref name="runRoot"/>, unless something is already there:
    /// both providers may start the same run at nearly the same moment. Never throws for an existing marker or folder of
    /// that name; a run left without its own marker is simply never swept.
    /// </summary>
    public static void WriteMarker(string runRoot, string runId)
    {
        var marker = Path.Combine(runRoot, MarkerFileName);
        try
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, new Marker(runId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            && (File.Exists(marker) || Directory.Exists(marker)))
        {
            // The other provider wrote it first, or something else holds the name.
        }
    }

    /// <summary>Whether <paramref name="runFolder"/> holds a marker, small and not a link, naming the folder itself.</summary>
    public static bool IsMarkedRun(string runFolder, StringComparison nameComparison)
    {
        var marker = new FileInfo(Path.Combine(runFolder, MarkerFileName));
        if (!marker.Exists || marker.LinkTarget is not null
            || marker.Attributes.HasFlag(FileAttributes.ReparsePoint) || marker.Length > MaxMarkerBytes)
            return false;

        try
        {
            var read = JsonSerializer.Deserialize<Marker>(File.ReadAllText(marker.FullName));
            return read is not null && string.Equals(read.RunId, Path.GetFileName(runFolder), nameComparison);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a path inside a run (relative, <c>/</c>-separated) is a file the providers write: a log, console or JSONL
    /// file at the run root or one phase folder down, or the manifest at the root. The marker is not included.
    /// </summary>
    public static bool IsOwnFile(string relativePath)
    {
        if (relativePath == ManifestFileName)
            return true;

        var parts = relativePath.Split('/');
        return parts.Length is 1 or 2 && OwnFileNames.Contains(parts[^1], StringComparer.Ordinal);
    }

    /// <summary>
    /// One plain folder name that maps to itself: no separators, no <c>.</c> or <c>..</c>, no characters a file name
    /// cannot hold, no trailing dot or space (Windows drops them, so the folder would not match its own marker), and no
    /// device name.
    /// </summary>
    private static void RequireFolderName(string name, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);
        var stem = name.Split('.')[0];
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\')
            || name.EndsWith('.') || name.EndsWith(' ')
            || ReservedDeviceNames.Contains(stem))
            throw new ArgumentException($"'{name}' is not a single folder name.", paramName);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record Marker(string RunId);
}
