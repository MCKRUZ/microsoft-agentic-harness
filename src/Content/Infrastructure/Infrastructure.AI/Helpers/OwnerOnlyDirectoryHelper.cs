namespace Infrastructure.AI.Helpers;

/// <summary>
/// Creates a directory (and any missing parents) with owner-only access on POSIX, for storage roots
/// that hold sensitive content — tool-call payloads, execution traces — and were never meant to be
/// readable by anything beyond the process's own user (#527).
/// </summary>
/// <remarks>
/// Windows is left to its inherited ACL. Deliberately NOT reused by <c>SandboxWorkspace</c>, which
/// grants broader access on purpose (a workspace mounted into a container running as a different UID
/// needs <c>Other</c> permissions) — that is a different security posture for a different reason, not
/// the same gap.
/// <para>
/// <strong>Does not remediate a directory that already exists</strong> (/code-review, round 2) — this
/// only tightens permissions on the create path. A host upgraded in place, whose storage root was
/// already created by pre-#527 code with looser default permissions, keeps that root's old mode
/// indefinitely; only the NEW leaf directories created under it after the upgrade get owner-only.
/// Closing that gap needs a one-time startup remediation pass — tracked in #670.
/// </para>
/// <para>
/// <strong>A non-cooperating writer racing to create the same new segment first (#648)</strong> no
/// longer wins permanently: every segment this call determines is missing gets its mode re-asserted
/// via <c>File.SetUnixFileMode</c> after the create call, not just trusted from the create call's
/// mode argument. That argument is silently ignored by the BCL whenever the segment already exists
/// by the time this call's own create runs — which, empirically (verified via a real concurrent-
/// racer control run against the pre-fix code), can only happen because of a writer that reached the
/// segment through something OTHER than a cooperating call to this same method: two callers of
/// <see cref="Create"/> racing each other can never leave a segment at the loose default, because
/// every caller requests the identical owner-only mode and <c>Directory.CreateDirectory</c> applies
/// the WINNING caller's requested mode atomically at creation. The re-assert exists for the writer
/// that does NOT request that mode — e.g. plain <c>Directory.CreateDirectory(path)</c> elsewhere in
/// the codebase, or a future caller that forgets to route through this helper.
/// </para>
/// </remarks>
internal static class OwnerOnlyDirectoryHelper
{
    private const UnixFileMode OwnerOnlyMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Test-only seam: when set, invoked with each segment immediately before this method's own
    /// creation attempt for it, letting a test deterministically simulate a concurrent, non-
    /// cooperating writer creating that exact segment first — instead of relying on real thread
    /// scheduling to land in a narrow timing window. Always <see langword="null"/> in production.
    /// </summary>
    internal static Action<string>? RaceSimulationHookForTests;

    /// <summary>
    /// Creates <paramref name="directory"/> (and any missing parents) with owner-only read/write/execute
    /// access on POSIX, applied to <em>every</em> directory this call actually creates — not just the
    /// leaf. A no-op permission-wise for any segment that already exists.
    /// </summary>
    /// <remarks>
    /// <c>Directory.CreateDirectory(path, mode)</c>'s single-call overload does NOT do this
    /// (/code-review finding, verified against <c>dotnet/runtime</c>'s <c>FileSystem.Unix.cs</c>):
    /// its internal <c>CreateParentsAndDirectory</c> only applies the caller's requested mode to the
    /// final path segment — every missing intermediate directory it creates along the way gets the
    /// BCL's loose default (0777 minus umask, typically 0755). On a first-ever call for a not-yet-
    /// existing storage root, that would leave every intermediate segment (e.g. the trace root, an
    /// "executions" folder, an execution-run-id folder) world-readable/executable — permanently, since
    /// nothing revisits their permissions once created — even though the leaf directory this method
    /// was actually asked to protect ends up correctly restricted. Walking the path manually and
    /// creating each missing segment with its own single-directory call (root-to-leaf, so a segment's
    /// parent always already exists by the time that segment's own call runs) is what closes this: each
    /// such call's "final path segment" is always the one directory it is responsible for, so the
    /// requested mode lands on every level, not just the last one.
    /// </remarks>
    public static void Create(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        var fullPath = Path.GetFullPath(directory);
        var missingSegments = new Stack<string>();
        var current = fullPath;
        while (!Directory.Exists(current))
        {
            missingSegments.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
                break;
            current = parent;
        }

        while (missingSegments.Count > 0)
        {
            var segment = missingSegments.Pop();
            RaceSimulationHookForTests?.Invoke(segment);
            Directory.CreateDirectory(segment, OwnerOnlyMode);

            // #648: a non-cooperating writer can win the race to create this exact segment first,
            // with the BCL's loose default mode — CreateDirectory is then a silent no-op for
            // permissions on an already-existing directory, so the mode argument above is not a
            // guarantee. Re-asserting the mode here, unconditionally, closes that.
            try
            {
                File.SetUnixFileMode(segment, OwnerOnlyMode);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // /code-review finding on this same fix: a legitimate concurrent cleanup sweep (e.g.
                // FileSystemToolResultStore's expiry prune, which deletes an empty result directory the
                // instant it observes one) can delete this just-created, still-empty segment in the gap
                // between the create call above and this one. Nothing is left to protect at that point —
                // not a failure of this call's job, and no worse than the pre-#648-fix behavior, where a
                // bare CreateDirectory never threw on this path either.
            }
        }
    }
}
