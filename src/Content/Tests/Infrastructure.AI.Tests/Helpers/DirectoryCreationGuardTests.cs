using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// Fails the build the moment a NEW <c>Infrastructure.AI</c> call site creates a directory without
/// routing through <see cref="Infrastructure.AI.Helpers.OwnerOnlyDirectoryHelper"/>, instead of relying
/// on the next reviewer to grep for it.
/// </summary>
/// <remarks>
/// <para>
/// This exact class of gap has been closed three separate times by ad hoc grep-and-patch: #527
/// (original fix + trace stores), #640 (7 more call sites), #660 (6 more) — each found by manually
/// repeating the same grep sweep. Nothing durable prevented a fourth (#674). This is the durable
/// version: every occurrence of a directory-creating BCL API (see <see cref="PlainCreateDirectory"/>
/// for the exact set matched) in <c>Infrastructure.AI</c> production source must be either the helper's
/// own implementation or a file named in <see cref="ExcludedFiles"/>, with a reason recorded next to it.
/// </para>
/// <para>
/// <strong>Fail-closed, not fail-quiet.</strong> A file that starts calling the plain BCL method and is
/// not already in <see cref="ExcludedFiles"/> fails this test by name — it does not silently pass
/// because the reviewer forgot to grep. Widening <see cref="ExcludedFiles"/> is a deliberate, reviewed
/// decision with a comment explaining the exclusion, not a default.
/// </para>
/// <para>
/// <strong>The allowlist is expected to shrink, not just grow.</strong> #671/#672/#673 track migrating
/// three of today's exclusions (the MetaHarness optimization-run directories, the structured/file
/// logger directories, and the sandbox base-path loop's <c>LogsBasePath</c> entry) to the owner-only
/// helper once a cross-layer exposure mechanism exists for callers outside <c>Infrastructure.AI</c>
/// (the helper is <see langword="internal"/> to this assembly). This test does not require the
/// allowlist to be minimal — only that every occurrence outside it is caught.
/// </para>
/// <para>
/// Scoped to <c>Infrastructure.AI</c> only, matching every prior sweep's own scope (#673 already
/// documents why <c>Infrastructure.AI.RAG</c>'s <c>KuzuGraphBackend</c> needs a separate cross-assembly
/// decision before it can even reach the internal helper).
/// </para>
/// </remarks>
public sealed class DirectoryCreationGuardTests
{
    /// <summary>
    /// Files under <c>Infrastructure.AI</c> allowed to call the plain BCL directory-creation method
    /// directly, each with the reason it is not routed through
    /// <see cref="Infrastructure.AI.Helpers.OwnerOnlyDirectoryHelper"/>. Paths are relative to the
    /// <c>Infrastructure.AI</c> project root, forward-slash separated.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExcludedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Helpers/OwnerOnlyDirectoryHelper.cs"] =
            "The trusted implementation itself — the Windows no-op path and the internal " +
            "segment-by-segment walk that the mode argument actually lands on.",

        ["DependencyInjection.cs"] =
            "The sandbox allowed-base-path loop: agent-directed file-system-tool workspace roots, " +
            "which intentionally do not get owner-only treatment (see SandboxWorkspace's own remarks " +
            "on why it grants broader access on purpose). Also currently sweeps in LogsBasePath, which " +
            "is NOT agent-directed and should not stay excluded on that basis — tracked in #672.",

        ["DependencyInjection.Conversations.cs"] =
            "The conversation database directory: deliberately excluded after review found it is " +
            "documented elsewhere as intentionally shared across hosts that may not run under the same " +
            "service account, which owner-only permissions would silently break (see PR #675's review " +
            "history).",

        ["Sandbox/DockerContainerLaunchPreparer.cs"] =
            "Docker sandbox workspace/parent directories mounted into a container that may run as a " +
            "different UID — needs broader access by design, the same posture SandboxWorkspace documents.",

        ["Sandbox/ProcessSandboxLaunchPreparer.cs"] =
            "Process sandbox workspace directory — same broader-access-by-design posture as the Docker " +
            "launch preparer above.",

        ["Sandbox/SandboxWorkspace.cs"] =
            "Explicitly documented in OwnerOnlyDirectoryHelper's own class remarks as deliberately NOT " +
            "reusing this helper.",

        ["Tools/FileSystemService.cs"] =
            "The write_file tool's own directory creation: agent-directed file I/O inside a path " +
            "already bounded by SandboxedPathGuard, not a harness-internal storage root.",
    }.AsReadOnly();

    /// <summary>
    /// Matches every BCL API this guard knows of that creates a directory outside
    /// <see cref="Infrastructure.AI.Helpers.OwnerOnlyDirectoryHelper"/>: <c>Directory.CreateDirectory</c>
    /// and <c>Directory.CreateTempSubdirectory</c> (optionally qualified with <c>System.IO.</c>, and with
    /// no trailing <c>\(</c> requirement for the former — a bare method group, e.g. passed as an
    /// <c>Action&lt;string&gt;</c>, would otherwise be a silent miss rather than the false positive this
    /// scan's own philosophy tolerates), <c>DirectoryInfo.CreateSubdirectory(</c> on any instance, and the
    /// inline idiom <c>new DirectoryInfo(...).Create()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The <c>DirectoryInfo</c> constructor argument must be nesting-aware</strong> (CI
    /// correctness-review finding, caught on the PR that introduced this test — a real defect, verified
    /// by probe file, not a false alarm): a first cut used <c>\([^)]*\)</c>, a flat, non-nesting match.
    /// A call whose own
    /// argument contains parentheses — <c>new DirectoryInfo(Path.Combine(a, b)).Create()</c>, the exact
    /// idiom this branch of the pattern exists to catch — has <c>[^)]*</c> stop at the FIRST inner
    /// <c>)</c>, leaving the real closing paren unconsumed and the whole alternative failing to match:
    /// a real directory-creating call site would pass this guard silently. The balancing-group construct
    /// below (<c>(?&lt;paren&gt;</c>/<c>(?&lt;-paren&gt;</c>/<c>(?(paren)(?!))</c>) matches parentheses at
    /// arbitrary nesting depth instead of assuming exactly one level — the identical technique, and the
    /// identical prior mistake, <see cref="SourceScan.FindTypeDeclarations"/> already documents for its
    /// own primary-constructor group.
    /// </para>
    /// <para>
    /// <strong>Residual gap (/code-review finding on the first cut, which matched only
    /// <c>Directory.CreateDirectory</c>):</strong> a <c>DirectoryInfo</c> obtained some other way — a
    /// stored variable, a method return value — and then <c>.Create()</c>d on a later, separate
    /// statement is not caught; matching bare <c>.Create()</c> on any receiver would flag every
    /// unrelated API of that shape in the codebase (<c>ILoggerFactory.Create</c>,
    /// <c>HttpRequestMessage.Create</c>, ...), which is not a workable trade for one more construct this
    /// scan cannot presently reach without real type information. Not live today (verified against the
    /// current tree) and, like every gap <see cref="SourceScan"/> itself documents, a false positive
    /// here would be a named file to review, never a silent miss.
    /// </para>
    /// </remarks>
    private static readonly Regex PlainCreateDirectory = new(
        @"\b(?:System\.IO\.)?Directory\.(?:CreateDirectory|CreateTempSubdirectory)\b" +
        @"|\.CreateSubdirectory\s*\(" +
        @"|\bnew\s+(?:System\.IO\.)?DirectoryInfo\s*\(" +
        @"(?:[^()]|(?<paren>\()|(?<-paren>\)))*(?(paren)(?!))\)\s*\.\s*Create\b",
        RegexOptions.Compiled);

    [Fact]
    public void EveryPlainDirectoryCreateDirectoryCall_IsOnTheDocumentedExclusionList()
    {
        var contentRoot = RepoRoot.Combine("src", "Content", "Infrastructure", "Infrastructure.AI");
        var sources = SourceScan.ReadProductionSources(contentRoot);
        sources.Should().NotBeEmpty("the scan must actually see Infrastructure.AI's source tree");

        var offendingFiles = sources
            .Where(s => PlainCreateDirectory.IsMatch(s.Code))
            .Select(s => Path.GetRelativePath(contentRoot, s.Path).Replace('\\', '/'))
            .Where(relative => !ExcludedFiles.ContainsKey(relative))
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "a new Infrastructure.AI call site is creating a directory without routing through " +
            "OwnerOnlyDirectoryHelper.Create — either migrate it, or add it to " +
            $"{nameof(DirectoryCreationGuardTests)}.{nameof(ExcludedFiles)} with a reviewed reason " +
            "(see #674)");
    }

    [Fact]
    public void EveryExcludedFile_StillExistsAndStillContainsAPlainCall()
    {
        // The inverse direction: a stale entry (the file was deleted, or migrated to the helper and
        // the exclusion never removed) doesn't create a security gap, but it does mean this guard's
        // own allowlist is no longer an accurate map of where the gap actually is — which is exactly
        // the kind of drift #674 exists to prevent from going unnoticed a second time.
        var contentRoot = RepoRoot.Combine("src", "Content", "Infrastructure", "Infrastructure.AI");
        var sources = SourceScan.ReadProductionSources(contentRoot)
            .ToDictionary(s => Path.GetRelativePath(contentRoot, s.Path).Replace('\\', '/'), s => s.Code, StringComparer.Ordinal);

        var staleEntries = ExcludedFiles.Keys
            .Where(relative => !sources.TryGetValue(relative, out var code) || !PlainCreateDirectory.IsMatch(code))
            .ToArray();

        staleEntries.Should().BeEmpty(
            "these files no longer exist, or no longer call the plain BCL method — remove the stale " +
            $"{nameof(ExcludedFiles)} entry so the allowlist keeps matching reality");
    }
}
