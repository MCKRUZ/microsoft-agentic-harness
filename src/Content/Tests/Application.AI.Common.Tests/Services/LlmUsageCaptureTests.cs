using Application.AI.Common.Services;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// Tests for the PR 6 invocation-capture path on <see cref="LlmUsageCapture"/>.
/// Args + stdout pairing by CallId is the load-bearing invariant for the
/// <c>/api/sessions/{id}/tools/{invocationId}</c> deep-link — these tests
/// guard against silent regressions there.
/// </summary>
public class LlmUsageCaptureTests
{
    private static LlmUsageCapture CreateSut()
    {
        var appConfig = new AppConfig();
        appConfig.Observability.LlmPricing.Models.Add(new ModelPricingEntry
        {
            Name = "test-model",
            InputPerMillion = 0m,
            OutputPerMillion = 0m,
            CacheReadPerMillion = 0m,
            CacheWritePerMillion = 0m,
        });
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);
        return new LlmUsageCapture(monitor.Object);
    }

    [Fact]
    public void TakeSnapshot_NoActivity_EmitsEmptyInvocations()
    {
        var sut = CreateSut();

        var snapshot = sut.TakeSnapshot();

        snapshot.ToolInvocations.Should().BeEmpty();
        snapshot.ToolNames.Should().BeEmpty();
    }

    [Fact]
    public void RecordToolRequestAndResult_SameCallId_MergesIntoSingleInvocation()
    {
        var sut = CreateSut();

        sut.RecordToolRequest("call-1", "ReadFile", "{\"path\":\"a.txt\"}");
        sut.RecordToolResult("call-1", "contents of a.txt");

        var snapshot = sut.TakeSnapshot();

        snapshot.ToolInvocations.Should().ContainSingle();
        var inv = snapshot.ToolInvocations[0];
        inv.CallId.Should().Be("call-1");
        inv.ToolName.Should().Be("ReadFile");
        inv.ArgsJson.Should().Be("{\"path\":\"a.txt\"}");
        inv.Stdout.Should().Be("contents of a.txt");
    }

    [Fact]
    public void RecordToolRequest_TwoDistinctCallIds_KeepsBothInvocations()
    {
        var sut = CreateSut();

        sut.RecordToolRequest("call-1", "ReadFile", "{\"path\":\"a.txt\"}");
        sut.RecordToolRequest("call-2", "ReadFile", "{\"path\":\"b.txt\"}");
        sut.RecordToolResult("call-2", "B");
        sut.RecordToolResult("call-1", "A");

        var snapshot = sut.TakeSnapshot();

        snapshot.ToolInvocations.Should().HaveCount(2);
        snapshot.ToolInvocations.Should().ContainSingle(i => i.CallId == "call-1" && i.Stdout == "A");
        snapshot.ToolInvocations.Should().ContainSingle(i => i.CallId == "call-2" && i.Stdout == "B");
    }

    [Fact]
    public void RecordToolRequest_NullCallId_StoresEachAsItsOwnInvocation()
    {
        var sut = CreateSut();

        sut.RecordToolRequest(null, "ReadFile", "{\"x\":1}");
        sut.RecordToolRequest(null, "ReadFile", "{\"x\":2}");

        var snapshot = sut.TakeSnapshot();

        snapshot.ToolInvocations.Should().HaveCount(2);
        snapshot.ToolInvocations.Should().AllSatisfy(i => i.CallId.Should().BeNull());
    }

    [Fact]
    public void RecordToolResult_NullCallId_IsIgnored()
    {
        var sut = CreateSut();

        sut.RecordToolResult(null, "orphan result");

        var snapshot = sut.TakeSnapshot();

        snapshot.ToolInvocations.Should().BeEmpty();
    }

    [Fact]
    public void RecordToolResult_UnknownCallId_PreservedAsPartialInvocation()
    {
        var sut = CreateSut();

        sut.RecordToolResult("orphan-id", "late arriving result");

        var snapshot = sut.TakeSnapshot();

        // The capture keeps orphan results so a future debug session can see
        // them, but the invocation with empty ToolName is filtered out of the
        // snapshot list (it's not a valid tool execution row to insert).
        snapshot.ToolInvocations.Should().BeEmpty();
    }

    [Fact]
    public void RecordToolRequest_EmptyToolName_IsIgnored()
    {
        var sut = CreateSut();

        sut.RecordToolRequest("call-1", string.Empty, "{}");

        var snapshot = sut.TakeSnapshot();

        snapshot.ToolInvocations.Should().BeEmpty();
    }

    [Fact]
    public void TakeSnapshot_ResetsInvocations()
    {
        var sut = CreateSut();
        sut.RecordToolRequest("call-1", "ReadFile", "{}");
        sut.RecordToolResult("call-1", "ok");

        _ = sut.TakeSnapshot();
        var second = sut.TakeSnapshot();

        second.ToolInvocations.Should().BeEmpty();
    }

    [Fact]
    public void RecordToolRequest_PopulatesToolNamesSetAsWell()
    {
        var sut = CreateSut();

        sut.RecordToolRequest("call-1", "ReadFile", null);
        sut.RecordToolRequest("call-2", "WriteFile", null);

        var snapshot = sut.TakeSnapshot();

        snapshot.ToolNames.Should().BeEquivalentTo(new[] { "ReadFile", "WriteFile" });
    }

    // ── Cost: a null per-call model must still be priced ──
    // Repro for the dashboard cost-tile bug. The chat client often does not surface
    // a per-call ModelId, so the recorded model is null and cost silently computes
    // to $0 — even though the configured DefaultModel (claude-sonnet-4-6) IS priced.
    // Verified in prod: 216/217 session_messages had model=(null) and cost 0.0000,
    // while the one row with a real model priced correctly.

    [Fact]
    public void TakeSnapshot_NullModel_FallsBackToConfiguredDefaultModel()
    {
        var sut = CreateSut(); // default config: DefaultModel = claude-sonnet-4-6

        sut.Record(inputTokens: 1_000_000, outputTokens: 0, cacheRead: 0, cacheWrite: 0, model: null);
        var snapshot = sut.TakeSnapshot();

        snapshot.Model.Should().Be("claude-sonnet-4-6",
            "a null per-call model must fall back to the configured DefaultModel so cost can be priced");
    }

    [Fact]
    public void TakeSnapshot_NullModel_PricesCostAtDefaultModelRate()
    {
        var sut = CreateSut(); // claude-sonnet-4-6 input rate = $3.00 / 1M tokens

        sut.Record(inputTokens: 1_000_000, outputTokens: 0, cacheRead: 0, cacheWrite: 0, model: null);
        var snapshot = sut.TakeSnapshot();

        snapshot.CostUsd.Should().Be(3.00m,
            "1M input tokens at the default model's $3.00/M rate — was $0 because a null model skipped pricing");
    }

    [Fact]
    public void TakeSnapshot_ExplicitModel_StillTakesPrecedenceOverDefault()
    {
        var sut = CreateSut(); // claude-haiku-4-5 input rate = $0.80 / 1M tokens

        sut.Record(inputTokens: 1_000_000, outputTokens: 0, cacheRead: 0, cacheWrite: 0, model: "claude-haiku-4-5");
        var snapshot = sut.TakeSnapshot();

        snapshot.Model.Should().Be("claude-haiku-4-5", "an explicitly recorded model must not be overridden by the default");
        snapshot.CostUsd.Should().Be(0.80m);
    }

    // ── #517: per-call usage, distinct from the accumulated totals above ──
    // The context-bar reconciliation needs one specific call's prompt size, not the turn's
    // accumulated total across every model call — a turn with two tool round-trips would
    // otherwise report roughly three prompts' worth against a snapshot of one.

    [Fact]
    public void TakeSnapshot_NoActivity_CallsIsEmpty()
    {
        var sut = CreateSut();

        var snapshot = sut.TakeSnapshot();

        snapshot.Calls.Should().BeEmpty();
    }

    [Fact]
    public void TakeSnapshot_MultipleCalls_RecordsEachSeparatelyInOrder()
    {
        var sut = CreateSut();

        sut.Record(inputTokens: 1_000, outputTokens: 50, cacheRead: 0, cacheWrite: 0, model: "test-model");
        sut.Record(inputTokens: 8_000, outputTokens: 200, cacheRead: 100, cacheWrite: 0, model: "test-model");
        sut.Record(inputTokens: 9_500, outputTokens: 300, cacheRead: 0, cacheWrite: 50, model: "test-model");

        var snapshot = sut.TakeSnapshot();

        snapshot.Calls.Should().HaveCount(3,
            "the accumulated total folds three calls into one number; reconciliation needs each on its own");
        snapshot.Calls[0].InputTokens.Should().Be(1_000);
        snapshot.Calls[1].InputTokens.Should().Be(8_000);
        snapshot.Calls[1].CacheRead.Should().Be(100);
        snapshot.Calls[2].InputTokens.Should().Be(9_500);
        snapshot.Calls[2].CacheWrite.Should().Be(50);

        // The accumulated totals must still be correct and independent of the per-call list.
        snapshot.InputTokens.Should().Be(1_000 + 8_000 + 9_500);
    }

    [Fact]
    public void TakeSnapshot_ResetsCalls()
    {
        var sut = CreateSut();
        sut.Record(inputTokens: 1_000, outputTokens: 0, cacheRead: 0, cacheWrite: 0, model: "test-model");

        _ = sut.TakeSnapshot();
        var second = sut.TakeSnapshot();

        second.Calls.Should().BeEmpty();
    }

    [Fact]
    public void LastCallPromptTokens_NoCalls_IsNull()
    {
        var sut = CreateSut();

        var snapshot = sut.TakeSnapshot();

        snapshot.LastCallPromptTokens.Should().BeNull();
    }

    [Fact]
    public void LastCallPromptTokens_SumsInputCacheReadAndCacheWrite_ForTheLastCallOnly()
    {
        var sut = CreateSut();

        sut.Record(inputTokens: 1_000, outputTokens: 0, cacheRead: 0, cacheWrite: 0, model: "test-model");
        sut.Record(inputTokens: 8_450, outputTokens: 0, cacheRead: 100, cacheWrite: 50, model: "test-model");

        var snapshot = sut.TakeSnapshot();

        snapshot.LastCallPromptTokens.Should().Be(8_450 + 100 + 50,
            "the first call's 1,000 tokens must not fold into the reconciliation figure — only the " +
            "last call's own prompt matters for #517");
    }
}
