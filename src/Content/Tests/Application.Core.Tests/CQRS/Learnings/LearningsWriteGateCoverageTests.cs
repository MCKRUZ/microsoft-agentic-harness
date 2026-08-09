using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Structural guard for issue #338: every handler that writes learning <em>content</em> must consult
/// the memory write gate.
/// </summary>
/// <remarks>
/// <para>
/// The learnings channel replays stored text into an agent's instructions, so unclassified content
/// entering it is a memory-poisoning vector. The gate was originally added to
/// <c>RememberCommandHandler</c> alone, on the belief that creation was the only way content enters
/// — which was wrong: <c>ImproveLearningCommandHandler</c> replaces a stored learning's text. That
/// miss is the reason this test exists rather than a comment asking future authors to remember.
/// </para>
/// <para>
/// This is a source scan, deliberately. A behavioural test can only cover handlers someone thought
/// to write a test for, and the failure being guarded against is precisely a handler nobody thought
/// about. It is the same shape as <c>SecurityControlHasACallerTests</c>, which exists because a
/// registered-but-never-invoked control shipped five separate times.
/// </para>
/// </remarks>
public sealed class LearningsWriteGateCoverageTests
{
    /// <summary>
    /// Captures what a <c>Content</c> assignment is assigned <em>from</em>.
    /// </summary>
    /// <remarks>
    /// Deliberately a capture rather than a negative lookahead. The lookahead form
    /// (<c>Content\s*=\s*(?!learning\.Content)</c>) is worthless here: <c>\s*</c> backtracks to match
    /// zero spaces, which moves the lookahead onto the whitespace, where it trivially succeeds — so
    /// it flags every assignment including the ones it was written to exclude. That was caught by
    /// this file's own mutation control, which is the reason the control exists.
    /// </remarks>
    private static readonly Regex ContentAssignment = new(
        @"\bContent\s*=\s*([\w.]+)", RegexOptions.Compiled);

    /// <summary>The one assignment that is not a content write: carrying the stored text forward.</summary>
    private const string CarriesExistingTextForward = "learning.Content";

    private static readonly Regex ConsultsGate = new(
        @"_writeGate\s*\.\s*EvaluateAsync", RegexOptions.Compiled);

    private static bool WritesContent(string source) =>
        ContentAssignment.Matches(source)
            .Any(m => m.Groups[1].Value != CarriesExistingTextForward);

    [Fact]
    public void EveryHandlerThatWritesLearningContent_ConsultsTheWriteGate()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in LearningsHandlerFiles())
        {
            var source = StripComments(File.ReadAllText(file));
            if (!WritesContent(source))
                continue;

            scanned++;
            if (!ConsultsGate.IsMatch(source))
                offenders.Add(Path.GetFileName(file));
        }

        scanned.Should().BeGreaterThan(0, "the scan must actually find the content-writing handlers");
        offenders.Should().BeEmpty(
            "a handler that writes learning content without the memory write gate lets unclassified "
            + "text into the channel that replays into agent instructions");
    }

    [Fact]
    public void TheScanRecognisesAnUngatedWrite()
    {
        // Mutation control: the assertion above is only evidence if the patterns actually match the
        // shape real handlers are written in. Both are checked against synthetic sources here, so a
        // regex that silently stops matching fails loudly instead of passing everything.
        const string ungated = "var updated = learning with { Content = request.NewText };";
        const string gated = "var d = await _writeGate.EvaluateAsync(key, content, type, ct);";

        WritesContent(ungated).Should().BeTrue();
        ConsultsGate.IsMatch(ungated).Should().BeFalse();
        ConsultsGate.IsMatch(gated).Should().BeTrue();

        // A pure feedback update that carries the existing text forward is not a content write and
        // must not be flagged, or the guard would demand a scan on the per-recall reinforcement path.
        WritesContent("Content = learning.Content").Should().BeFalse();
        WritesContent("Content=learning.Content").Should().BeFalse();

        // ...but a conditional replacement still is one, even though the fallback carries text
        // forward. This is the exact shape ImproveLearningCommandHandler writes.
        WritesContent("Content = request.ReinforcementContent ?? learning.Content").Should().BeTrue();
    }

    private static IEnumerable<string> LearningsHandlerFiles()
    {
        var directory = Path.Combine(RepoRoot(), "src", "Content", "Application", "Application.Core", "CQRS", "Learnings");
        Directory.Exists(directory).Should().BeTrue($"expected the learnings CQRS folder at {directory}");

        return Directory.EnumerateFiles(directory, "*Handler.cs", SearchOption.AllDirectories);
    }

    /// <summary>
    /// Walks up to the repository root. In a git worktree <c>.git</c> is a file rather than a
    /// directory, so both are accepted.
    /// </summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    /// <summary>
    /// Removes line and block comments so a handler that only <em>mentions</em> writing content in
    /// its documentation is not scanned as if it did.
    /// </summary>
    private static string StripComments(string source) =>
        Regex.Replace(
            Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//.*?$", string.Empty, RegexOptions.Multiline);
}
