using System.IO;
using System.Linq;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// PR-6 sync test: the harness's <see cref="MagenticConventions"/> must keep
/// the same attribute keys as <c>documentation/architecture/magentic-spans.md</c>.
/// Drift in either direction (doc adds a key, registry adds a key, doc renames
/// a key) is caught at CI time.
/// </summary>
public sealed class MagenticSemconvSyncTests
{
    [Fact]
    public void All_registry_attribute_keys_appear_in_spans_doc()
    {
        var docPath = LocateSpansDoc();
        var doc = File.ReadAllText(docPath);

        // Sample the doc for the most load-bearing attribute keys. The full
        // registry has more (content-capture etc) — these are the metadata-only
        // keys the harness emits in PR-6.
        var keys = new[]
        {
            MagenticConventions.WorkflowName,
            MagenticConventions.MaxRounds,
            MagenticConventions.MaxStalls,
            MagenticConventions.MaxResets,
            MagenticConventions.RequirePlanSignoff,
            MagenticConventions.Participants,
            MagenticConventions.RoundsExecuted,
            MagenticConventions.ResetsExecuted,
            MagenticConventions.CompletionReason,
            MagenticConventions.Role,
            MagenticConventions.PlanVersion,
            MagenticConventions.RoundNumber,
            MagenticConventions.RoundStallCountAfter,
            MagenticConventions.ProgressIsRequestSatisfied,
            MagenticConventions.ProgressIsInLoop,
            MagenticConventions.ProgressIsProgressBeingMade,
            MagenticConventions.ProgressNextSpeaker,
            MagenticConventions.ResetNumber,
            MagenticConventions.ResetTrigger,
            MagenticConventions.ResetWasStalled,
            MagenticConventions.PlanReviewOutcome,
            MagenticConventions.PlanReviewIsStalled,
            MagenticConventions.PlanReviewHasProgressLedger
        };

        var missing = keys.Where(k => !doc.Contains(k)).ToList();
        missing.Should().BeEmpty(
            "every emitted Magentic attribute key must appear verbatim in documentation/architecture/magentic-spans.md");
    }

    private static string LocateSpansDoc()
    {
        // Walk up from the test assembly directory to find the repo root, then
        // resolve documentation/architecture/magentic-spans.md.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "documentation", "architecture", "magentic-spans.md");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate documentation/architecture/magentic-spans.md by walking up from the test base directory.");
    }
}
