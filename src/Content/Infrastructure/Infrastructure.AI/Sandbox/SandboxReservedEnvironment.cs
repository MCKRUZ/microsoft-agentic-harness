namespace Infrastructure.AI.Sandbox;

/// <summary>
/// The dynamic-linker-hijack environment variable names, shared by <see cref="ProcessSandboxLaunchPreparer"/>
/// and <see cref="DockerContainerLaunchPreparer"/> rather than each carrying its own copy. Before
/// this type existed, the two preparers held byte-identical name lists guarded only by a comment
/// asking a future maintainer to remember to update both — exactly the shape #371's shared-preparer
/// extraction otherwise eliminated everywhere else. A per-request environment grant colliding with
/// one of these names would let a request load an arbitrary shared library into the sandboxed
/// process before its own code ever runs; see each preparer's own remarks for why the two tiers
/// need the guard for different reasons (a process tier's host-inherited environment vs. a
/// container tier's writable bind mount).
/// </summary>
internal static class SandboxReservedEnvironment
{
    /// <summary>
    /// Names both platforms' dynamic linkers consult before a program's own entry point runs.
    /// </summary>
    internal static readonly string[] DynamicLinkerNames =
    [
        "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "LD_ORIGIN_PATH",
        "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH", "DYLD_FRAMEWORK_PATH"
    ];

    /// <summary>
    /// Returns the first per-request environment grant whose name collides (case-insensitively —
    /// Windows environment lookups are case-insensitive) with <paramref name="reservedNames"/>, or
    /// null when all grants are benign.
    /// </summary>
    internal static string? FindReservedGrant(
        IReadOnlyList<string> reservedNames, IReadOnlyDictionary<string, string>? environmentVariables)
    {
        if (environmentVariables is null)
            return null;

        return environmentVariables.Keys.FirstOrDefault(
            name => reservedNames.Contains(name, StringComparer.OrdinalIgnoreCase));
    }
}
