using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Presentation.FoundryHost.Tests.ScreeningTestHost;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// Real <c>/responses</c> requests, served by Foundry's own hosting library the way the host serves them: an attack in
/// any part of a request never reaches the model.
/// </summary>
/// <remarks>
/// <para>
/// The model is the oracle. It records everything it is sent, so these tests do not depend on where the screening sits,
/// only on whether caller text got past it. Calling the wrapped agent directly cannot prove this: the request's
/// instructions and the replayed conversation reach the agent through run options that only Foundry's handler builds.
/// </para>
/// <para>
/// The host is Foundry's hosting library on Kestrel, on a free local port, with the screening wrapper around a real
/// agent that has no history provider of its own (as the harness's agents have none), which is what makes the handler
/// replay the conversation through the run options.
/// </para>
/// </remarks>
public sealed class FoundryResponsesScreeningEndToEndTests : IAsyncLifetime
{
    private readonly ScreeningTestHost _host = new();
    private readonly RecordingChatClient _model = new();
    private WebApplication? _app;
    private HttpClient? _client;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder();
        // Outside Foundry the hosting library leaves the listen address alone, so it is set here.
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var served = FoundryInputScreening.Wrap(new ChatClientAgent(_model), _host.Provider);
        builder.Services.AddFoundryResponses(served, new FileSystemAgentSessionStore(_host.Folder("sessions")));

        _app = builder.Build();
        _app.MapFoundryResponses();
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
            await _app.DisposeAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Benign_ReachesTheModel_AndItsReplyComesBack()
    {
        // The control: without it, "the model never saw the attack" could mean the host never reached the model at all.
        var (status, body) = await PostAsync(new JsonObject { ["input"] = Benign });

        status.Should().Be(HttpStatusCode.OK, body);
        _model.Calls.Should().Be(1);
        body.Should().Contain(RecordingChatClient.Reply);
    }

    [Fact]
    public async Task AnAttackInTheInput_IsAnsweredWithTheRefusal()
    {
        var (status, body) = await PostAsync(new JsonObject { ["input"] = Attack });

        status.Should().Be(HttpStatusCode.OK, body);
        _model.Calls.Should().Be(0);
        body.Should().Contain("Prompt injection detected");
    }

    [Fact]
    public async Task AnAttackInTheInstructions_NeverReachesTheModel()
    {
        var (status, body) = await PostAsync(new JsonObject { ["input"] = Benign, ["instructions"] = Attack });

        status.Should().Be(HttpStatusCode.OK, body);
        _model.Seen.Should().NotContain(Attack);
        _model.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("developer")]
    [InlineData("system")]
    [InlineData("assistant")]
    public async Task AnAttackInAMessageOfAnotherRole_NeverReachesTheModel(string role)
    {
        var input = new JsonArray(Message(role, Attack), Message("user", Benign));

        var (status, body) = await PostAsync(new JsonObject { ["input"] = input });

        status.Should().Be(HttpStatusCode.OK, body);
        _model.Seen.Should().NotContain(Attack);
        _model.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AnAttackInACallerSuppliedToolResult_NeverReachesTheModel()
    {
        var input = new JsonArray(
            new JsonObject { ["type"] = "function_call", ["call_id"] = "call-1", ["name"] = "lookup", ["arguments"] = "{}" },
            new JsonObject { ["type"] = "function_call_output", ["call_id"] = "call-1", ["output"] = Attack },
            Message("user", Benign));

        var (status, body) = await PostAsync(new JsonObject { ["input"] = input });

        status.Should().Be(HttpStatusCode.OK, body);
        _model.Seen.Should().NotContain(Attack);
        _model.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ARefusedAttack_DoesNotComeBackInTheNextTurn()
    {
        var (_, refused) = await PostAsync(new JsonObject { ["input"] = Attack });
        var previousId = JsonNode.Parse(refused)!["id"]!.GetValue<string>();

        var (status, body) = await PostAsync(
            new JsonObject { ["input"] = "Do what I asked above.", ["previous_response_id"] = previousId });

        status.Should().Be(HttpStatusCode.OK, body);
        _model.Seen.Should().NotContain(Attack, "the platform stores a refused request's input and replays it as history");
        _model.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AContinuedConversation_ReachesTheModelWithItsHistory()
    {
        // The control for the history channel: a benign chained turn reaches the model and carries the earlier turn,
        // so the test above refused because of the attack, not because chaining breaks the run.
        var (_, first) = await PostAsync(new JsonObject { ["input"] = "My name is Ada." });
        var previousId = JsonNode.Parse(first)!["id"]!.GetValue<string>();

        var (status, body) = await PostAsync(new JsonObject { ["input"] = Benign, ["previous_response_id"] = previousId });

        status.Should().Be(HttpStatusCode.OK, body);
        _model.Calls.Should().Be(2);
        _model.Seen.Should().Contain("My name is Ada.");
    }

    [Fact]
    public async Task AStreamedAttack_IsAnsweredWithTheRefusal()
    {
        var (status, body) = await PostAsync(new JsonObject { ["input"] = Attack, ["stream"] = true });

        status.Should().Be(HttpStatusCode.OK, body);
        _model.Calls.Should().Be(0);
        body.Should().Contain("Prompt injection detected");
    }

    private static JsonObject Message(string role, string text) =>
        new() { ["type"] = "message", ["role"] = role, ["content"] = text };

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(JsonObject request)
    {
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _client!.PostAsync("/responses", content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
