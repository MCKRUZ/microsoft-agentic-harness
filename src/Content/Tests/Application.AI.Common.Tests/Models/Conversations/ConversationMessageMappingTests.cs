using System.Text.Json;
using Application.AI.Common.Models.Conversations;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Models.Conversations;

/// <summary>
/// Pins the invariant a security review of #249 item 6 / PR2 identified as load-bearing but
/// mechanically unguarded: <see cref="ConversationMessageMapping.ToChatMessages"/> must keep
/// producing text-only replayed history for as long as this harness ships. The moment it starts
/// projecting a persisted <see cref="ConversationMessage.ToolCalls"/> record into a real
/// <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> — which is the whole
/// point of #249 item 6 — a currently-dormant defect in
/// <c>ToolDiagnosticsMiddleware.AppendFunctionResultTracesAsync</c> (it scans inbound messages for
/// tool results, and cannot yet distinguish "genuinely new this turn" from "replayed from an
/// earlier turn") goes live: every replayed historical tool result would be re-recorded to the
/// trace store as if it had just happened again. This test's only job is to fail loudly the moment
/// that mapping changes, rather than let the defect activate silently.
/// </summary>
public sealed class ConversationMessageMappingTests
{
    [Fact]
    public void ToChatMessages_ProducesTextContentOnly_SoReplayedHistoryCannotReenterToolTracing()
    {
        var toolCall = new ToolCallRecord(
            "search",
            JsonDocument.Parse("""{"query":"weather"}""").RootElement,
            JsonDocument.Parse("""{"result":"sunny"}""").RootElement,
            DurationMs: 42);

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.User, "what's the weather?", DateTimeOffset.UtcNow),
            new(
                Guid.NewGuid(), MessageRole.Assistant, "it's sunny", DateTimeOffset.UtcNow,
                ToolCalls: [toolCall]),
            new(Guid.NewGuid(), MessageRole.Tool, "sunny", DateTimeOffset.UtcNow),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        replayed.SelectMany(m => m.Contents).Should().AllBeOfType<TextContent>(
            "ToolDiagnosticsMiddleware.AppendFunctionResultTracesAsync scans inbound messages and " +
            "would re-record a replayed FunctionResultContent as if it had just happened again — " +
            "see #249 item 6 and the comment on ToolDiagnosticsMiddleware.AppendFunctionResultTracesAsync");
    }
}
