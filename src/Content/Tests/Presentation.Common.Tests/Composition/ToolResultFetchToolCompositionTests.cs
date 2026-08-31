using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Tools;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Proves <c>tool_result_fetch</c> (#521) actually works on the REAL composition root — both that it
/// resolves the way every production caller resolves a keyed tool, and that it can retrieve a result
/// end to end once a request's own scope is established.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Root-provider resolution is the regression test, not an afterthought.</strong> An earlier
/// version of <see cref="ToolResultFetchTool"/> was registered <c>AddKeyedScoped</c>, reasoning (only
/// half correctly) that it needed the calling request's own <see cref="IAgentExecutionContext"/>. Every
/// production caller of a keyed <see cref="ITool"/> — <c>ToolChainBuilder.ResolveToolByName</c>,
/// <c>FirstPartyToolLookup.Resolve</c> — is itself a SINGLETON holding the ROOT provider, so that
/// registration threw <see cref="InvalidOperationException"/> on every turn of every skill that lists
/// this tool, under the <c>ValidateScopes = true</c> every host enables. Caught by this repo's own
/// <c>correctness</c> gate before it shipped. The first test below resolves the SAME way those two
/// real callers do — directly from the root provider, no created scope — specifically so this exact
/// defect can never silently return.
/// </para>
/// <para>
/// <see cref="ToolResultFetchTool_WithAnEstablishedAmbientScope_RetrievesASpilledResult"/> proves the
/// actual fix: the tool resolves the calling request's <see cref="IAgentExecutionContext"/> from
/// <see cref="IAmbientRequestScope.Current"/> at execution time, not construction — the pattern this
/// codebase already uses for every other singleton that needs per-request scoped state.
/// </para>
/// </remarks>
public sealed class ToolResultFetchToolCompositionTests
{
    [Fact]
    public void ToolResultFetchTool_ResolvesFromTheRootProvider_UnderScopeValidation()
    {
        using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>());

        var act = () => provider.GetRequiredKeyedService<ITool>(ToolResultFetchTool.ToolName);

        act.Should().NotThrow(
            "ToolChainBuilder and FirstPartyToolLookup both resolve keyed tools from the root " +
            "provider — a scoped registration for this tool would throw here in production");
        act().Should().BeOfType<ToolResultFetchTool>(
            "the model's only way to retrieve a spilled result is this exact keyed tool");
    }

    [Fact]
    public async Task ToolResultFetchTool_WithAnEstablishedAmbientScope_RetrievesASpilledResult()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "tool-result-fetch-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Below the shipped 50,000-char default, StoreIfLargeAsync keeps content inline and never
            // writes a file — RetrievePageAsync would then correctly report "not found" for content
            // that was never persisted. Lower the threshold so a small test string still spills.
            using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
            {
                ["AppConfig:AI:ContextManagement:ToolResultStorage:PerResultCharLimit"] = "50",
                ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = tempDir,
            });
            var tool = provider.GetRequiredKeyedService<ITool>(ToolResultFetchTool.ToolName);

            using var requestScope = provider.CreateScope();
            var executionContext = requestScope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
            executionContext.Initialize(agentId: "test-agent", conversationId: "conv-1", turnNumber: 1);

            var resultStore = provider.GetRequiredService<IToolResultStore>();
            var storedText = new string('x', 200);
            var stored = await resultStore.StoreIfLargeAsync(
                executionContext.ToolResultScopeId, "some_tool", operation: null, storedText);
            stored.FullContentPath.Should().NotBeNull(
                "the test setup itself must actually spill to disk, or this proves nothing");

            var ambientScope = provider.GetRequiredService<IAmbientRequestScope>();
            using (ambientScope.BeginScope(requestScope.ServiceProvider))
            {
                // #563: the tool returns one bounded page per call (half of PerResultCharLimit here —
                // 25 chars — always strictly smaller than whatever was large enough to spill in the
                // first place), so proving end-to-end retrieval means walking every page the way the
                // model itself would, following each page's own "call again with offset=N" trailer.
                var retrieved = "";
                var offset = 0;
                while (true)
                {
                    var result = await tool.ExecuteAsync(
                        "fetch",
                        new Dictionary<string, object?> { ["resultId"] = stored.ResultId, ["offset"] = offset });

                    result.Success.Should().BeTrue(
                        "the singleton tool must resolve the CALLING request's own scope via the ambient " +
                        "request scope, not fail just because it isn't constructor-injected anymore");

                    var pageText = result.Output!;
                    var trailerStart = pageText.IndexOf("\n[page ends at", StringComparison.Ordinal);
                    if (trailerStart < 0)
                    {
                        retrieved += pageText;
                        break;
                    }

                    retrieved += pageText[..trailerStart];
                    var trailer = pageText[trailerStart..];
                    offset = int.Parse(
                        trailer[(trailer.LastIndexOf("offset=", StringComparison.Ordinal) + 7)..].TrimEnd(']'));
                }

                retrieved.Should().Be(storedText);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ToolResultFetchTool_WithNoAmbientScopeEstablished_FailsRatherThanThrows()
    {
        // The direct-invoke surface never establishes an ambient scope (this tool is deliberately
        // excluded from it — see its own remarks), and any other caller that resolves this tool
        // outside the MediatR pipeline must degrade the same way: a clear failure, not a NullReferenceException.
        using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>());
        var tool = provider.GetRequiredKeyedService<ITool>(ToolResultFetchTool.ToolName);

        var result = await tool.ExecuteAsync(
            "fetch", new Dictionary<string, object?> { ["resultId"] = Guid.NewGuid().ToString("N") });

        result.Success.Should().BeFalse("there is no calling request scope to resolve a retrieval scope from");
    }
}
