using System.Text.Json;
using Domain.AI.Context;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Presentation.AgentHub.Hubs;
using Presentation.AgentHub.Notifications;
using Xunit;

namespace Presentation.AgentHub.Tests.Notifications;

/// <summary>
/// SignalR wire contract tests for the Foresight context-snapshot pipeline.
/// The JS dashboard subscribes with
/// <c>connection.on("ContextSnapshot", payload =&gt; payload.conversationId)</c>
/// so the SERIALISED JSON property names are part of the wire contract.
/// These tests serialise the payload with the same camelCase policy SignalR's
/// JsonHubProtocol applies, then assert against the resulting JSON keys —
/// catches any DTO rename that would silently break the dashboard.
/// </summary>
public sealed class SignalRContextSnapshotNotifierTests
{
    private readonly Mock<IHubContext<AgentTelemetryHub>> _hub = new();
    private readonly Mock<IClientProxy> _client = new();
    private readonly Mock<IHubClients> _hubClients = new();
    private readonly SignalRContextSnapshotNotifier _sut;

    // Matches the JsonHubProtocol default in ASP.NET Core SignalR.
    private static readonly JsonSerializerOptions s_wireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SignalRContextSnapshotNotifierTests()
    {
        _hubClients.Setup(h => h.Group(It.IsAny<string>())).Returns(_client.Object);
        _hub.SetupGet(h => h.Clients).Returns(_hubClients.Object);
        _sut = new SignalRContextSnapshotNotifier(
            _hub.Object,
            NullLogger<SignalRContextSnapshotNotifier>.Instance);
    }

    private static ContextSnapshot NewSnapshot(
        string conversationId = "conv-1",
        int turnIndex = 3,
        string turnId = "t-03") =>
        new(
            conversationId,
            turnIndex,
            turnId,
            new CategoryBreakdown(100, 200, 300, 400, 500, 600),
            [
                new LoadedItem("User message", 12, ContextCategory.Messages, null),
                new LoadedItem("Tool: Read", 34, ContextCategory.Tools, "BillingPipeline.cs"),
            ],
            new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    private async Task<JsonElement> CaptureBroadcastPayloadAsync()
    {
        object[]? capturedArgs = null;
        _client.Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        await _sut.NotifyAsync(NewSnapshot(), CancellationToken.None);

        capturedArgs.Should().NotBeNull();
        capturedArgs!.Should().HaveCount(1);
        var json = JsonSerializer.Serialize(capturedArgs[0], s_wireJson);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task Broadcasts_to_per_conversation_group_with_correct_event_name()
    {
        await _sut.NotifyAsync(NewSnapshot("conv-42"), CancellationToken.None);

        _hubClients.Verify(h => h.Group("conversation:conv-42"), Times.Once);
        _client.Verify(c => c.SendCoreAsync(
            AgentTelemetryHub.EventContextSnapshot,
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Payload_top_level_property_names_pinned_for_JS_client_contract()
    {
        var payload = await CaptureBroadcastPayloadAsync();

        var keys = payload.EnumerateObject().Select(p => p.Name).ToHashSet();
        keys.Should().BeEquivalentTo([
            "conversationId",
            "turnIndex",
            "turnId",
            "ctxAfter",
            "loaded",
            "capturedAtUtc",
        ]);
    }

    [Fact]
    public async Task CtxAfter_payload_contains_all_six_category_keys_lowercase()
    {
        var payload = await CaptureBroadcastPayloadAsync();

        var ctxAfter = payload.GetProperty("ctxAfter");
        var keys = ctxAfter.EnumerateObject().Select(p => p.Name).ToHashSet();
        keys.Should().BeEquivalentTo([
            "system", "agents", "skills", "tools", "mcp", "messages",
        ]);
    }

    [Fact]
    public async Task Loaded_items_emit_cat_as_lowercase_category_name()
    {
        var payload = await CaptureBroadcastPayloadAsync();

        var loaded = payload.GetProperty("loaded");
        loaded.GetArrayLength().Should().Be(2);

        var first = loaded[0];
        var firstKeys = first.EnumerateObject().Select(p => p.Name).ToHashSet();
        firstKeys.Should().BeEquivalentTo(["what", "tokens", "cat", "ref"]);
        first.GetProperty("cat").GetString().Should().Be("messages");

        var second = loaded[1];
        second.GetProperty("cat").GetString().Should().Be("tools");
        second.GetProperty("ref").GetString().Should().Be("BillingPipeline.cs");
    }

    [Fact]
    public async Task Swallows_transport_failure_to_honour_notifier_contract()
    {
        _client.Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub gone"));

        var act = () => _sut.NotifyAsync(NewSnapshot(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Propagates_OperationCanceledException_from_transport()
    {
        _client.Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _sut.NotifyAsync(NewSnapshot(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
