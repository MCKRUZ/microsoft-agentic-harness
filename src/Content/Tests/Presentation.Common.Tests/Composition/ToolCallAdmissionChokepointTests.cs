using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Asserts that the composed admission chain is the <strong>only</strong> production caller of the
/// four tool-call gates.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a source scan and not a unit test.</strong> The defect this guards against is not a
/// wrong answer from any one component — every component was correct every time. It is a <em>new call
/// site</em> that asks the gates itself and gets the set or the order wrong. That has happened four
/// times: a gate was added to one of the five execution paths and forgotten on the others, most
/// recently leaving a consumer's own safety rule live in a chat turn and absent from the identical
/// call issued from a plan. No unit test can fail for code that was never written, so the check has to
/// be over the source itself.
/// </para>
/// <para>
/// <strong>Why not reflection.</strong> The three ways a caller can reach a gate — constructor
/// injection, <c>GetRequiredService</c> off a scope, and an ambient static — do not all leave a trace
/// reflection can see. A constructor parameter is visible; a service-locator call inside a method body
/// is not. Reading the source catches all three.
/// </para>
/// <para>
/// <strong>What is allowed to name a gate.</strong> The gate interfaces themselves, their
/// implementations, the chain that composes them, and the DI registration. Everything else must go
/// through <c>IToolCallAdmissionPipeline</c>. Adding a file to the allowlist is the moment to ask
/// whether a fifth hand-rolled sequence is really what is wanted — which is the conversation this test
/// exists to force.
/// </para>
/// </remarks>
public sealed class ToolCallAdmissionChokepointTests
{
    /// <summary>The gates that must only ever be invoked from inside the chain.</summary>
    private static readonly string[] GateInterfaces =
    [
        "IAgentToolAuthorizationGate",
        "IToolInvocationGovernor",
        "IToolClassificationGate",
        "IToolCallObserverChain",
        "IProgressEvaluator"
    ];

    /// <summary>
    /// Files permitted to name a gate: the contracts, the implementations, the chain, and the
    /// registration. Matched on file name, which is unique across these directories.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // The contracts themselves.
        "IAgentToolAuthorizationGate.cs",
        "IToolInvocationGovernor.cs",
        "IToolClassificationGate.cs",
        "IToolCallObserverChain.cs",
        "IToolCallObserver.cs",
        "IProgressEvaluator.cs",

        // The chain, and the accessor that publishes it.
        "IToolCallAdmissionPipeline.cs",
        "ToolCallAdmissionPipeline.cs",
        "ToolAdmissionAccessor.cs",

        // The implementations.
        "DefaultAgentToolAuthorizationGate.cs",
        "ToolInvocationGovernor.cs",
        "ToolInvocationGovernor.Approval.cs",
        "DefaultToolClassificationGate.cs",
        "ToolCallObserverChain.cs",
        "ProgressEvaluator.cs",

        // Registration.
        "DependencyInjection.cs"
    };

    /// <summary>
    /// Extensions whose contents are compiled code. Comments are stripped before matching, so a doc
    /// comment mentioning a gate is not a violation — only code is.
    /// </summary>
    private const string SourceGlob = "*.cs";

    [Fact]
    public void TheAdmissionChainIsTheOnlyProductionCallerOfTheGates()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(contentRoot, SourceGlob, SearchOption.AllDirectories))
        {
            if (SourceScan.IsExcluded(file, contentRoot) || Allowed.Contains(Path.GetFileName(file)))
                continue;

            var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(file));
            var named = GateInterfaces.Where(gate => Regex.IsMatch(code, $@"\b{gate}\b")).ToArray();
            if (named.Length > 0)
                offenders.Add($"{Path.GetRelativePath(contentRoot, file)} → {string.Join(", ", named)}");
        }

        offenders.Should().BeEmpty(
            "every execution path must reach the gates through IToolCallAdmissionPipeline. A file that "
            + "names a gate directly is a sixth hand-rolled sequence waiting to drift from the other "
            + "five — which is the defect that shipped four times before the chain existed. Offenders:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void TheGuardWouldActuallyFire()
    {
        // The scan above passes trivially if the matching is broken — an empty offender list is the
        // same shape whether nothing violates the rule or nothing is being read. This proves the
        // matcher recognises a violation, and that comments do not count as one.
        var violating = SourceScan.StripCommentsAndStrings(
            "public class X { private readonly IToolInvocationGovernor _g; }");
        var commentOnly = SourceScan.StripCommentsAndStrings(
            "// see IToolInvocationGovernor for the first stage\npublic class X { }");

        Regex.IsMatch(violating, @"\bIToolInvocationGovernor\b").Should().BeTrue();
        Regex.IsMatch(commentOnly, @"\bIToolInvocationGovernor\b").Should().BeFalse();
    }

    [Fact]
    public void TheScanReadsARepresentativeNumberOfFiles()
    {
        // Guards the other direction: a wrong root or a broken glob makes the scan pass by reading
        // nothing at all. The floor is far below the real count (~1,400) so ordinary churn cannot
        // trip it, but a scan that found nothing would.
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        Directory.EnumerateFiles(contentRoot, SourceGlob, SearchOption.AllDirectories)
            .Count(f => !SourceScan.IsExcluded(f, contentRoot))
            .Should().BeGreaterThan(500);
    }
}
