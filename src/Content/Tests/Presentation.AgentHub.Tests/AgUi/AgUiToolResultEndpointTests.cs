using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Tests.Controllers;
using System.Net;
using System.Net.Http.Json;
using Xunit;
using Application.AI.Common.Interfaces.AI;

namespace Presentation.AgentHub.Tests.AgUi;

/// <summary>
/// Integration tests for <c>POST /ag-ui/tool-result</c> — the resume endpoint that completes a mid-run
/// client round-trip. Drives the real auth + ownership pipeline against the SAME singleton
/// <see cref="PendingToolCallRegistry"/> the endpoint resolves, by taking both the client and the
/// registry from one configured server (each <c>WithWebHostBuilder</c> builds its own provider).
/// </summary>
public sealed class AgUiToolResultEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AgUiToolResultEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private WebApplicationFactory<Program> AuthFactory() =>
        _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { })));

    private static HttpClient ClientAs(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        return client;
    }

    [Fact]
    public async Task PostToolResult_OwnerWithPendingCall_Returns200_AndCompletesTheCall()
    {
        var factory = AuthFactory();
        var store = factory.Services.GetRequiredService<IConversationStore>();
        var registry = factory.Services.GetRequiredService<PendingToolCallRegistry>();

        var owner = $"tr-owner-{Guid.NewGuid():N}";
        var conv = await store.CreateAsync("dashboard-agent", owner);
        var callId = Guid.NewGuid().ToString("N");
        var pending = registry.RegisterAsync(callId, conv.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        using var client = ClientAs(factory, owner);
        var response = await client.PostAsJsonAsync("/ag-ui/tool-result",
            new { threadId = conv.Id, callId, result = "applied" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("applied",
            "the awaiting tool must observe the posted result");
    }

    [Fact]
    public async Task PostToolResult_ForeignUser_Returns403_AndLeavesCallPending()
    {
        var factory = AuthFactory();
        var store = factory.Services.GetRequiredService<IConversationStore>();
        var registry = factory.Services.GetRequiredService<PendingToolCallRegistry>();

        var owner = $"tr-owner-{Guid.NewGuid():N}";
        var attacker = $"tr-attacker-{Guid.NewGuid():N}";
        var conv = await store.CreateAsync("dashboard-agent", owner);
        var callId = Guid.NewGuid().ToString("N");
        _ = registry.RegisterAsync(callId, conv.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        using var client = ClientAs(factory, attacker);
        var response = await client.PostAsJsonAsync("/ag-ui/tool-result",
            new { threadId = conv.Id, callId, result = "injected" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        registry.PendingCount.Should().BeGreaterThan(0, "a rejected post must not complete the pending call");
    }

    [Fact]
    public async Task PostToolResult_CallIdFromAnotherThread_Returns404_EvenWhenCallerOwnsThePostedThread()
    {
        var factory = AuthFactory();
        var store = factory.Services.GetRequiredService<IConversationStore>();
        var registry = factory.Services.GetRequiredService<PendingToolCallRegistry>();

        // The caller owns BOTH conversations; the call is pending on convA, but they post it against convB.
        var owner = $"tr-owner-{Guid.NewGuid():N}";
        var convA = await store.CreateAsync("dashboard-agent", owner);
        var convB = await store.CreateAsync("dashboard-agent", owner);
        var callId = Guid.NewGuid().ToString("N");
        _ = registry.RegisterAsync(callId, convA.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        using var client = ClientAs(factory, owner);
        var response = await client.PostAsJsonAsync("/ag-ui/tool-result",
            new { threadId = convB.Id, callId, result = "mismatched" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        registry.PendingCount.Should().BeGreaterThan(0, "the call is bound to convA and must stay pending");
    }

    [Fact]
    public async Task PostToolResult_UnknownCallId_Returns404()
    {
        var factory = AuthFactory();
        var store = factory.Services.GetRequiredService<IConversationStore>();

        var owner = $"tr-owner-{Guid.NewGuid():N}";
        var conv = await store.CreateAsync("dashboard-agent", owner);

        using var client = ClientAs(factory, owner);
        var response = await client.PostAsJsonAsync("/ag-ui/tool-result",
            new { threadId = conv.Id, callId = "never-registered", result = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostToolResult_UnknownConversation_Returns404()
    {
        var factory = AuthFactory();
        var owner = $"tr-owner-{Guid.NewGuid():N}";
        using var client = ClientAs(factory, owner);

        var response = await client.PostAsJsonAsync("/ag-ui/tool-result",
            new { threadId = Guid.NewGuid().ToString(), callId = "c1", result = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
