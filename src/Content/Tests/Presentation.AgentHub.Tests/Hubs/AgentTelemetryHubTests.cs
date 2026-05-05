using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.AgentHub.Hubs;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.DTOs;
using System.Collections.Concurrent;
using Xunit;

namespace Presentation.AgentHub.Tests.Hubs;

/// <summary>
/// Integration tests for <see cref="AgentTelemetryHub"/>.
/// Uses an in-process test server with HTTP long-polling transport (WebSocket requires a real socket server).
/// <see cref="TestWebApplicationFactory.MockMediator"/> controls agent responses to prevent real AI calls.
/// </summary>
public sealed class AgentTelemetryHubTests : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly WebApplicationFactory<Program> _authedFactory;
    private readonly IConversationStore _store;

    /// <summary>
    /// Initialises the hub test class. Creates a shared authenticated factory variant
    /// that adds <see cref="TestAuthHandler"/> as the default authentication scheme.
    /// </summary>
    public AgentTelemetryHubTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _authedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { })));

        // Singleton store is shared between base factory and authenticated factory
        // because ConfigureTestServices runs for both.
        _store = factory.Services.GetRequiredService<IConversationStore>();
    }

    /// <inheritdoc/>
    public void Dispose() => _authedFactory.Dispose();

    // ── Infrastructure ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a SignalR connection to the authenticated test server.
    /// The connection uses long polling so it works inside <see cref="TestServer"/>.
    /// </summary>
    private HubConnection CreateConnection(string userId = "test-user", string? roles = null)
    {
        var server = _authedFactory.Server;
        return new HubConnectionBuilder()
            .WithUrl($"{server.BaseAddress}hubs/agent", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[TestAuthHandler.UserIdHeader] = userId;
                if (roles is not null)
                    options.Headers[TestAuthHandler.RolesHeader] = roles;
            })
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            })
            .Build();
    }

    // ── Authentication ────────────────────────────────────────────────────────

    /// <summary>
    /// The negotiate endpoint returns 401 for connections without authentication credentials,
    /// causing <see cref="HubConnection.StartAsync"/> to throw.
    /// </summary>
    [Fact]
    public async Task UnauthenticatedConnection_IsRejected()
    {
        // Use the base factory: TestJwtBearerHandler → NoResult → 401 challenge.
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl($"{server.BaseAddress}hubs/agent", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                // No auth headers → negotiate returns 401.
            })
            .Build();

        var ex = await Record.ExceptionAsync(() => connection.StartAsync());

        ex.Should().NotBeNull("negotiate endpoint returns 401 for unauthenticated requests");
        connection.State.Should().Be(HubConnectionState.Disconnected);
        await connection.DisposeAsync();
    }

    // ── Role gates ────────────────────────────────────────────────────────────

    /// <summary>JoinGlobalTraces throws HubException when the caller lacks the required role.</summary>
    [Fact]
    public async Task JoinGlobalTraces_WithoutRole_ThrowsHubException()
    {
        var connection = CreateConnection(roles: null);
        await connection.StartAsync();
        try
        {
            var ex = await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync("JoinGlobalTraces"));
            ex.Message.Should().Contain("AgentHub.Traces.ReadAll");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>JoinGlobalTraces succeeds when the caller carries the required role.</summary>
    [Fact]
    public async Task JoinGlobalTraces_WithRole_Succeeds()
    {
        var connection = CreateConnection(roles: "AgentHub.Traces.ReadAll");
        await connection.StartAsync();
        try
        {
            // Must not throw.
            await connection.InvokeAsync("JoinGlobalTraces");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    // ── Ownership (IDOR) ──────────────────────────────────────────────────────

    /// <summary>StartConversation throws HubException when the conversation belongs to a different user.</summary>
    [Fact]
    public async Task StartConversation_WithAnotherUsersConversationId_ThrowsHubException()
    {
        var otherConv = await _store.CreateAsync("test-agent", "other-user");
        var connection = CreateConnection("test-user");
        await connection.StartAsync();
        try
        {
            await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync("StartConversation", "test-agent", otherConv.Id));
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>SendMessage throws HubException when the conversation belongs to a different user.</summary>
    [Fact]
    public async Task SendMessage_AnotherUsersConversation_ThrowsHubException()
    {
        var otherConv = await _store.CreateAsync("test-agent", "other-user");
        var connection = CreateConnection("test-user");
        await connection.StartAsync();
        try
        {
            await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync("SendMessage", otherConv.Id, Guid.NewGuid(), "Hello"));
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>JoinConversationGroup throws HubException when the conversation belongs to a different user.</summary>
    [Fact]
    public async Task JoinConversationGroup_AnotherUsersConversation_ThrowsHubException()
    {
        var otherConv = await _store.CreateAsync("test-agent", "other-user");
        var connection = CreateConnection("test-user");
        await connection.StartAsync();
        try
        {
            await Assert.ThrowsAsync<HubException>(() =>
                connection.InvokeAsync("JoinConversationGroup", otherConv.Id));
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    // ── Chat flow ─────────────────────────────────────────────────────────────

    /// <summary>StartConversation creates and persists a new conversation record in the store.</summary>
    [Fact]
    public async Task StartConversation_CreatesNewConversationRecord()
    {
        var conversationId = Guid.NewGuid().ToString();
        var connection = CreateConnection("test-user");
        await connection.StartAsync();
        try
        {
            await connection.InvokeAsync("StartConversation", "test-agent", conversationId);

            var record = await _store.GetAsync(conversationId);
            record.Should().NotBeNull();
            record!.UserId.Should().Be("test-user");
            record.AgentName.Should().Be("test-agent");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// StartConversation returns at most MaxHistoryMessages messages for an existing conversation
    /// (last 20 of 25 stored).
    /// </summary>
    [Fact]
    public async Task StartConversation_ExistingConversation_ReturnsHistoryCappedAt20()
    {
        var conversationId = Guid.NewGuid().ToString();
        var record = await _store.CreateAsync("test-agent", "test-user", conversationId: conversationId);
        for (var i = 0; i < 25; i++)
            await _store.AppendMessageAsync(record.Id,
                new ConversationMessage(Guid.NewGuid(), MessageRole.User, $"msg-{i}", DateTimeOffset.UtcNow));

        var connection = CreateConnection("test-user");
        await connection.StartAsync();
        try
        {
            var history = await connection.InvokeAsync<List<ConversationMessage>>(
                "StartConversation", "test-agent", conversationId);

            history.Should().HaveCount(20, "MaxHistoryMessages is 20; last 20 of 25 should be returned");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>SendMessage emits at least one TokenReceived event before TurnComplete fires.</summary>
    [Fact]
    public async Task SendMessage_EmitsTokenReceivedEventsBeforeTurnComplete()
    {
        _factory.MockMediator.Reset();
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = true,
                Response = "This is a mock response that is intentionally longer than fifty characters.",
                UpdatedHistory = [],
            });

        var tokensReceived = new ConcurrentQueue<string>();
        var turnCompleteTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var connection = CreateConnection("test-user");
        connection.On<object>(AgentTelemetryHub.EventTokenReceived, _ => tokensReceived.Enqueue("token"));
        connection.On<object>(AgentTelemetryHub.EventTurnComplete, _ => turnCompleteTcs.TrySetResult(true));

        await connection.StartAsync();
        try
        {
            var conversationId = Guid.NewGuid().ToString();
            await connection.InvokeAsync("StartConversation", "test-agent", conversationId);
            await connection.InvokeAsync("SendMessage", conversationId, Guid.NewGuid(), "Hello");
            await turnCompleteTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

            tokensReceived.Should().NotBeEmpty(
                "at least one TokenReceived event must be emitted before TurnComplete");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// When the mediator throws, an Error event is emitted with a sanitized message —
    /// no exception details or stack traces are surfaced to the client.
    /// </summary>
    [Fact]
    public async Task SendMessage_OnMediatorException_EmitsErrorEventWithSanitizedMessage()
    {
        _factory.MockMediator.Reset();
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Internal implementation detail — must not leak"));

        var errorTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = CreateConnection("test-user");
        connection.On<object>(AgentTelemetryHub.EventError,
            payload => errorTcs.TrySetResult(payload?.ToString() ?? string.Empty));

        await connection.StartAsync();
        try
        {
            var conversationId = Guid.NewGuid().ToString();
            await connection.InvokeAsync("StartConversation", "test-agent", conversationId);
            await connection.InvokeAsync("SendMessage", conversationId, Guid.NewGuid(), "Trigger error");
            var errorPayload = await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

            errorPayload.Should().NotContain("Internal implementation detail",
                "exception messages must never be surfaced to hub clients");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// When the mediator throws, a synthetic error message is appended to the conversation store
    /// so the conversation record reflects the failed turn.
    /// </summary>
    [Fact]
    public async Task SendMessage_OnMediatorException_AppendsSyntheticErrorMessageToStore()
    {
        _factory.MockMediator.Reset();
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent failure"));

        var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = CreateConnection("test-user");
        connection.On<object>(AgentTelemetryHub.EventError, _ => errorTcs.TrySetResult(true));

        await connection.StartAsync();
        try
        {
            var conversationId = Guid.NewGuid().ToString();
            await connection.InvokeAsync("StartConversation", "test-agent", conversationId);
            await connection.InvokeAsync("SendMessage", conversationId, Guid.NewGuid(), "Trigger error");
            await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var record = await _store.GetAsync(conversationId);
            record.Should().NotBeNull();
            record!.Messages.Should().Contain(m =>
                m.Role == MessageRole.Assistant && m.Content.Contains("[Error]"),
                "a synthetic error assistant message must be appended for the failed turn");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Two concurrent SendMessage calls on the same conversation both complete successfully.
    /// The per-conversation semaphore ensures sequential execution without interleaving.
    /// </summary>
    [Fact]
    public async Task TwoRapidSendMessageCalls_BothCompleteSuccessfully()
    {
        _factory.MockMediator.Reset();
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = true,
                Response = "Response",
                UpdatedHistory = [],
            });

        var connection = CreateConnection("test-user");
        await connection.StartAsync();
        try
        {
            var conversationId = Guid.NewGuid().ToString();
            await connection.InvokeAsync("StartConversation", "test-agent", conversationId);

            // Both invocations on the same conversationId; the semaphore serialises them.
            var task1 = connection.InvokeAsync("SendMessage", conversationId, Guid.NewGuid(), "First message");
            var task2 = connection.InvokeAsync("SendMessage", conversationId, Guid.NewGuid(), "Second message");
            await Task.WhenAll(task1, task2);

            _factory.MockMediator.Verify(
                m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2),
                "mediator must be called once per SendMessage invocation");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }
}
