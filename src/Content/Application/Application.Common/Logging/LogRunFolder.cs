using System.Text.Json;

namespace Application.Common.Logging;

/// <summary>
/// The run folders the file log providers write under <c>Logging:LogsBasePath</c>: how a run and its phase are named, the
/// marker that proves a folder is a log run, and the files a run holds (#901).
/// </summary>
/// <remarks>
/// <para>
/// The marker exists for the retention sweep. The logs folder is also a root the file-system tool may write into, so a
/// folder that merely holds a <c>log.txt</c> is no proof the providers wrote it; one whose marker names the folder is.
/// Runs written before the marker existed have none and are never swept.
/// </para>
/// <para>
/// A run id and a phase are each one folder name. Before this they were checked only by a prefix comparison that a
/// sibling folder passed (<c>../logsX</c> resolves outside <c>logs</c> yet starts with its path).
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

    private static readonly string[] OwnFileNames = [LogFileName, ConsoleFileName, StructuredFileName];

    /// <summary>
    /// Resolves a run's folder, and its phase folder when there is one, refusing any name that is not a single folder
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

    /// <summary>
    /// Writes the marker naming <paramref name="runId"/> at <paramref name="runRoot"/>, unless one is already there: both
    /// providers may start the same run at nearly the same moment.
    /// </summary>
    public static void WriteMarker(string runRoot, string runId)
    {
        try
        {
            using var stream = new FileStream(
                Path.Combine(runRoot, MarkerFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, new Marker(runId));
        }
        catch (IOException) when (File.Exists(Path.Combine(runRoot, MarkerFileName)))
        {
            // The other provider wrote it first.
        }
    }

    /// <summary>Whether <paramref name="runFolder"/> holds a marker naming the folder itself.</summary>
    public static bool IsMarkedRun(string runFolder, StringComparison nameComparison)
    {
        var marker = Path.Combine(runFolder, MarkerFileName);
        if (!File.Exists(marker))
            return false;

        try
        {
            var read = JsonSerializer.Deserialize<Marker>(File.ReadAllText(marker));
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

    private static void RequireFolderName(string name, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException($"'{name}' is not a single folder name.", paramName);
    }

    private sealed record Marker(string RunId);
}
