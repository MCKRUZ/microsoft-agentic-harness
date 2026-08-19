using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Fences in who may call <c>MediatorDispatchRunner.RunAsync</c>, so a new caller cannot slip past
/// review without its <c>failureContext</c> argument being checked for scrubbing.
/// </summary>
/// <remarks>
/// <para>
/// <c>MediatorDispatchRunner.RunAsync</c> logs its <c>failureContext</c> parameter verbatim on a
/// dispatch failure (see #444) — the method's own contract says scrubbing anything
/// credential-bearing out of it is the <em>caller's</em> job, not something the method itself
/// inspects or redacts. Today's two callers honor that: <c>DocumentIngestTool</c> passes only the
/// scheme/host/port/path of a document URI (no query string, no userinfo), and
/// <c>WorkspaceWriteFileTool</c> passes an already-validated workspace-relative path. Nothing stops
/// a third caller from passing something unscrubbed.
/// </para>
/// <para>
/// <strong>Why a source scan and not a runtime guard.</strong> A source scan can't verify an
/// argument's <em>content</em> is scrubbed — that's a judgment call a reviewer makes reading the
/// caller's code, the same way <c>ToolCallAdmissionChokepointTests</c> can't verify a gate is
/// consulted correctly, only that nothing outside the chain claims to consult it at all. What a scan
/// <em>can</em> do is force the conversation: adding a file to this allowlist is the moment to ask
/// whether the new <c>failureContext</c> argument is safe the same way the existing two are.
/// </para>
/// </remarks>
public sealed class MediatorDispatchRunnerCallerAllowlistTests
{
    /// <summary>
    /// Files permitted to call <c>MediatorDispatchRunner.RunAsync</c>. <c>MediatorDispatchRunner.cs</c>
    /// itself is not listed — it declares <c>RunAsync</c>, it never calls it by the qualified name the
    /// scan matches.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "DocumentIngestTool.cs",
        "WorkspaceWriteFileTool.cs"
    };

    private const string CallPattern = @"\bMediatorDispatchRunner\.RunAsync\b";
    private const string SourceGlob = "*.cs";

    [Fact]
    public void OnlyTheAllowlistedToolsCallMediatorDispatchRunner()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(contentRoot, SourceGlob, SearchOption.AllDirectories))
        {
            if (SourceScan.IsExcluded(file, contentRoot) || Allowed.Contains(Path.GetFileName(file)))
                continue;

            var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(file));
            if (Regex.IsMatch(code, CallPattern))
                offenders.Add(Path.GetRelativePath(contentRoot, file));
        }

        offenders.Should().BeEmpty(
            "a new MediatorDispatchRunner.RunAsync caller must be added to this test's allowlist "
            + "deliberately — that is the moment to verify its failureContext argument is scrubbed "
            + "the same way DocumentIngestTool's and WorkspaceWriteFileTool's are. Offenders:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void TheGuardWouldActuallyFire()
    {
        // Proves the matcher recognizes a violation and that a comment mentioning the call does not
        // count as one — the same shape ToolCallAdmissionChokepointTests uses to prove its own scan
        // isn't passing because nothing is being read.
        var violating = SourceScan.StripCommentsAndStrings(
            "return await MediatorDispatchRunner.RunAsync(scopeFactory, dispatch, logger, name, ctx, ct);");
        var commentOnly = SourceScan.StripCommentsAndStrings(
            "// delegates to MediatorDispatchRunner.RunAsync under the hood\npublic class X { }");

        Regex.IsMatch(violating, CallPattern).Should().BeTrue();
        Regex.IsMatch(commentOnly, CallPattern).Should().BeFalse();
    }
}
