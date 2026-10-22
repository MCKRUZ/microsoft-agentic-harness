namespace Infrastructure.AI.Helpers;

/// <summary>
/// The file-system half of an age-based retention sweep (#901): lists a folder's subfolders without following links,
/// tells how long since anything in a unit (one run, workflow or similar) was written, and deletes the files a store
/// itself writes.
/// </summary>
/// <remarks>
/// <para>
/// Shared so that every file store's deletion follows the same rules, which are what keep a sweep from removing what it
/// does not own:
/// </para>
/// <list type="bullet">
///   <item>Links (symbolic links, junctions) are never followed or deleted; a folder holding one is never emptied of it.</item>
///   <item>A unit's age is its <em>newest</em> file, so a unit anything is still writing to is never old.</item>
///   <item>Only files the caller names as its own are deleted; anything else, such as a file an agent wrote into the
///   same folder, survives, and so does every directory it sits in.</item>
///   <item>The file that marks a folder as a unit is deleted last, once every other own file is gone, so a delete that
///   fails part-way leaves the unit findable and the next sweep finishes it.</item>
///   <item>Directories are removed only once empty.</item>
/// </list>
/// <para>
/// Which folders are units is the store's decision: only it knows its own layout and naming, and a folder that merely
/// holds a file of the right name is not proof the store wrote it.
/// </para>
/// </remarks>
internal static class RetentionDirectories
{
    private static readonly EnumerationOptions OneLevelNoLinks = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// <summary>The subfolders of <paramref name="directory"/>, links excluded; none when it does not exist or is a link.</summary>
    public static IEnumerable<string> Subdirectories(string directory) =>
        Directory.Exists(directory) && !IsLink(directory)
            ? Directory.EnumerateDirectories(directory, "*", OneLevelNoLinks).Where(child => !IsLink(child))
            : [];

    /// <summary>
    /// When anything in <paramref name="unit"/> was last written: the newest file at any depth (links skipped), or the
    /// directory itself when it holds no files.
    /// </summary>
    /// <remarks>
    /// Folder times are not counted when there are files: deleting a file restamps its folder, so a unit a sweep could
    /// only partly delete would otherwise look new and wait a whole further period before the next sweep finished it.
    /// </remarks>
    public static DateTime NewestWriteUtc(string unit)
    {
        DateTime? newest = null;
        foreach (var file in Files(unit))
        {
            var written = File.GetLastWriteTimeUtc(file);
            if (newest is null || written > newest)
                newest = written;
        }

        return newest ?? Directory.GetLastWriteTimeUtc(unit);
    }

    /// <summary>
    /// Deletes the files in <paramref name="unit"/>, at any depth, that <paramref name="isOwnFile"/> accepts (given the
    /// path relative to the unit, with <c>/</c> separators); then the unit's <paramref name="markerFileName"/>; then
    /// every directory left empty, the unit included.
    /// </summary>
    /// <returns>Whether the marker was deleted, so the folder is no longer a unit.</returns>
    /// <exception cref="IOException">A file could not be deleted; the marker is still in place.</exception>
    /// <exception cref="UnauthorizedAccessException">A file could not be deleted; the marker is still in place.</exception>
    public static bool DeleteOwnFiles(string unit, string markerFileName, Func<string, bool> isOwnFile)
    {
        foreach (var file in Files(unit).ToList())
        {
            var relative = Path.GetRelativePath(unit, file).Replace('\\', '/');
            if (relative != markerFileName && isOwnFile(relative))
                File.Delete(file);
        }

        var marker = Path.Combine(unit, markerFileName);
        if (!File.Exists(marker))
            return false;

        File.Delete(marker);
        RemoveEmptyDirectories(unit);
        return true;
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
            && parent.StartsWith(stop + Path.DirectorySeparatorChar, PathComparison)
            && Directory.Exists(parent)
            && !IsLink(parent)
            && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
            parent = Path.GetDirectoryName(parent);
        }
    }

    private static IEnumerable<string> Files(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", OneLevelNoLinks))
            yield return file;

        // Each folder is checked for being a link as it is entered, not only when listed, so one swapped for a link in
        // between is not followed.
        foreach (var child in Subdirectories(directory))
        {
            foreach (var file in Files(child))
                yield return file;
        }
    }

    private static void RemoveEmptyDirectories(string directory)
    {
        foreach (var child in Subdirectories(directory).ToList())
            RemoveEmptyDirectories(child);

        // A link is skipped by the enumeration above but still counts as an entry here, so a directory holding one is
        // never removed.
        if (!IsLink(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
