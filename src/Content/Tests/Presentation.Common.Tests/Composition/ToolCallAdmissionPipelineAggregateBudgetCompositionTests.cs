using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Governance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Proves the #522 aggregate-budget fix on the REAL composition root — specifically the interaction
/// with #521's spill store that a mock-based unit test cannot see.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this needs the real <c>FileSystemToolResultStore</c>, not a mock.</strong> A
/// <c>/code-review</c> pass on the #522 change caught a real regression:
/// <c>ToolCallAdmissionPipeline.SpillAndBuildMarkerAsync</c> is only ever reached once the pipeline's
/// own cut has already decided a result must be truncated — but the aggregate per-message budget can
/// shrink the ceiling a single result is cut to well below the store's own
/// <c>PerResultCharLimit</c>. Before the fix, <c>FileSystemToolResultStore.StoreIfLargeAsync</c>
/// re-derived "is this worth persisting" from <c>PerResultCharLimit</c> alone — a perfectly
/// normal-sized result (well under that limit) that was cut only because the turn's aggregate budget
/// ran out would be judged "too small to bother spilling," come back with no file on disk, and the
/// pipeline would silently fall back to the plain truncation marker: recoverable data, permanently
/// lost. <c>Application.AI.Common.Tests</c>'s own pipeline tests all use
/// <c>AdmissionHarness.PersistedResultStore()</c>, a mock that spills unconditionally — exactly the
/// shortcut that let this regression through review once already. This test resolves the real store
/// from the real DI graph specifically so that shortcut cannot happen twice.
/// </para>
/// </remarks>
public sealed class ToolCallAdmissionPipelineAggregateBudgetCompositionTests
{
    [Fact]
    public async Task ApplyOutputPolicy_CutPurelyByTheAggregateBudget_StillSpillsToTheRealStoreAndIsRetrievable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "admission-aggregate-budget-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // PerResultCharLimit stays at a realistic, generous size — the 10,000-char result below
            // never exceeds it on its own. AggregatePerMessageCharLimit is set far smaller so the cut
            // is caused ONLY by the aggregate budget, reproducing the exact gap code review found: a
            // result well under the store's own spill threshold that still needs to be recoverable.
            using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
            {
                ["AppConfig:AI:ContextManagement:ToolResultStorage:PerResultCharLimit"] = "50000",
                ["AppConfig:AI:ContextManagement:ToolResultStorage:AggregatePerMessageCharLimit"] = "6000",
                ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = tempDir,
            });

            using var requestScope = provider.CreateScope();
            var executionContext = requestScope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
            executionContext.Initialize(agentId: "test-agent", conversationId: "conv-1", turnNumber: 1);

            var pipeline = requestScope.ServiceProvider.GetRequiredService<IToolCallAdmissionPipeline>();
            // Cycling plain English words, no digits or punctuation shapes: varied enough that the real
            // ICompositeResponseSanitizer's "long repeated run" injection heuristic does not fire (an
            // earlier version of this test used a single repeated character and was replaced with a
            // short "[SANITIZED:injection]" placeholder before any cut this test exercises could
            // happen), and free of anything the at-rest IContentRedactionFilter's phone/email/secret
            // patterns could match (an earlier version used zero-padded digit sequences and a real
            // phone-shaped run was redacted, breaking the exact round-trip this test asserts).
            var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet" };
            var originalText = string.Concat(Enumerable.Range(0, 3_000).Select(i => words[i % words.Length] + " "));

            var result = await pipeline.ApplyOutputPolicyAsync(
                ToolCallAdmission.Allow(), "test_tool", originalText, CancellationToken.None);

            var marker = result.Should().BeOfType<string>().Which;
            marker.Should().Contain("tool_result_fetch",
                "10,000 chars is far under the 50,000-char PerResultCharLimit — the ONLY reason this "
                + "was cut at all is the 6,000-char aggregate budget, and that must still produce a "
                + "real retrieval id, not the bare fallback marker the pre-fix store silently returned");

            var resultId = marker[(marker.LastIndexOf("id=", StringComparison.Ordinal) + 3)..].TrimEnd(']');
            var resultStore = requestScope.ServiceProvider.GetRequiredService<IToolResultStore>();

            // #563: retrieval is now paged, not a single whole-file read — walk every page so this test
            // still proves the fix's whole point end to end: content cut purely by the aggregate budget
            // must be fully recoverable via tool_result_fetch, exactly as content cut by the per-result
            // ceiling already was, one page at a time.
            var retrieved = "";
            var offset = 0;
            while (true)
            {
                var page = await resultStore.RetrievePageAsync(
                    resultId, executionContext.ToolResultScopeId, offset, maxChars: 5_000, CancellationToken.None);
                retrieved += page.Text;
                if (!page.HasMore) break;
                offset = page.NextOffset;
            }

            retrieved.Should().Be(originalText,
                "the fix's whole point: content cut purely by the aggregate budget must still be fully "
                + "recoverable through tool_result_fetch, exactly as content cut by the per-result "
                + "ceiling already was");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
