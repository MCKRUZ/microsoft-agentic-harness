namespace Infrastructure.AI.Helpers;

/// <summary>
/// The file-system half of an age-based retention sweep (#901): finds a store's units (one directory per run, workflow
/// or similar), tells how long since anything in one was written, and deletes the files the store itself writes.
/// </summary>
/// <remarks>
/// <para>
/// Shared so that every file store's deletion follows the same rules, which are what keep a sweep from removing what it
/// does not own:
/// </para>
/// <list type="bullet">
///   <item>Links (symbolic links, junctions) are never followed or deleted; a unit holding one is never emptied of it.</item>
///   <item>A unit's age is its <em>newest</em> file, so a unit anything is still writing to is never old.</item>
///   <item>Only files the caller names as its own are deleted; anything else, such as a file an agent wrote into the
///   same folder, survives, and so does every directory it sits in.</item>
///   <item>Directories are removed only once empty.</item>
/// </list>
/// </remarks>
internal static class RetentionDirectories
{
    private static readonly EnumerationOptions OneLevelNoLinks = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// <summary>
    /// The directories under <paramref name="root"/>, to <paramref name="maxDepth"/> levels, that directly contain
    /// <paramref name="markerFileName"/>. A unit's own subdirectories are not searched for further units.
    /// </summary>
    public static IReadOnlyList<string> FindUnits(string root, string markerFileName, int maxDepth)
    {
        var units = new List<string>();
        if (Directory.Exists(root) && !IsLink(root))
            Collect(root, markerFileName, maxDepth, units);
        return units;
    }

    /// <summary>
    /// When anything in <paramref name="unit"/> was last written: the newest file at any depth (links skipped), or the
    /// directory itself when it holds no files.
    /// </summary>
    public static DateTime NewestWriteUtc(string unit)
    {
        var newest = Directory.GetLastWriteTimeUtc(unit);
        foreach (var file in Files(unit))
        {
            var written = File.GetLastWriteTimeUtc(file);
            if (written > newest)
                newest = written;
        }

        return newest;
    }

    /// <summary>
    /// Deletes the files in <paramref name="unit"/>, at any depth, that <paramref name="isOwnFile"/> accepts (given the
    /// path relative to the unit, with <c>/</c> separators), then every directory left empty, the unit included.
    /// </summary>
    /// <returns>Whether the unit itself is gone.</returns>
    public static bool DeleteOwnFiles(string unit, Func<string, bool> isOwnFile)
    {
        foreach (var file in Files(unit))
        {
            var relative = Path.GetRelativePath(unit, file).Replace('\\', '/');
            if (isOwnFile(relative))
                File.Delete(file);
        }

        RemoveEmptyDirectories(unit);
        return !Directory.Exists(unit);
    }

    /// <summary>
    /// Removes <paramref name="directory"/>'s parents while they are empty, stopping at <paramref name="root"/>, so a
    /// deleted unit does not leave a trail of empty folders.
    /// </summary>
    public static void RemoveEmptyParents(string directory, string root)
    {
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var parent = Path.GetDirectoryName(Path.GetFullPath(directory));
        while (parent is not null
            && parent.Length > stop.Length
            && parent.StartsWith(stop + Path.DirectorySeparatorChar, PathComparison)
            && Directory.Exists(parent)
            && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
            parent = Path.GetDirectoryName(parent);
        }
    }

    private static void Collect(string directory, string marker, int depthLeft, List<string> units)
    {
        if (File.Exists(Path.Combine(directory, marker)))
        {
            units.Add(directory);
            return;
        }

        if (depthLeft <= 0)
            return;

        foreach (var child in Directory.EnumerateDirectories(directory, "*", OneLevelNoLinks))
            Collect(child, marker, depthLeft - 1, units);
    }

    private static IEnumerable<string> Files(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", OneLevelNoLinks))
            yield return file;

        foreach (var child in Directory.EnumerateDirectories(directory, "*", OneLevelNoLinks))
        {
            foreach (var file in Files(child))
                yield return file;
        }
    }

    private static void RemoveEmptyDirectories(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory, "*", OneLevelNoLinks).ToList())
            RemoveEmptyDirectories(child);

        // A link is skipped by the enumeration above but still counts as an entry here, so a directory holding one is
        // never removed.
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsLink(string path) =>
        new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
}
