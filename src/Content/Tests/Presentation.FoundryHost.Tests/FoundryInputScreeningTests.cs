using Application.AI.Common.Exceptions;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static Presentation.FoundryHost.Tests.ScreeningTestHost;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// The hosted path screens what a caller sends, as every other host does. Foundry's runtime calls the agent directly, so
/// the turn pipeline whose prompt-injection scan and content safety screen every other host's input never runs here.
/// Without <see cref="FoundryInputScreening"/>, an attack or harmful text reached the model unscreened.
/// </summary>
/// <remarks>
/// These call the wrapped agent directly, one test per way caller text reaches it. The run-option channels (the
/// request's instructions and the replayed history) are built here as Foundry's handler builds them;
/// <see cref="FoundryResponsesScreeningEndToEndTests"/> proves the handler really uses them.
/// </remarks>
public sealed class FoundryInputScreeningTests : IDisposable
{
    private readonly ScreeningTestHost _host = new();
    private readonly RecordingChatClient _model = new();
    private readonly AIAgent _served;

    /// <summary>Wraps a real agent over a recording model, as the host serves it.</summary>
    public FoundryInputScreeningTests() =>
        _served = FoundryInputScreening.Wrap(new ChatClientAgent(_model), _host.Provider);

    /// <inheritdoc />
    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task RunAsync_APromptInjection_IsRefusedBeforeTheModelSeesIt()
    {
        var response = await _served.RunAsync([new ChatMessage(ChatRole.User, Attack)]);

        _model.Calls.Should().Be(0, "the scan refuses the attack before the agent runs");
        response.Text.Should().Contain("Prompt injection detected");
    }

    [Fact]
    public async Task RunAsync_TextContentSafetyBlocks_IsRefusedBeforeTheModelSeesIt()
    {
        var response = await _served.RunAsync([new ChatMessage(ChatRole.User, Harmful)]);

        _model.Calls.Should().Be(0, "content safety refuses the text before the agent runs");
        response.Text.Should().Contain(ContentSafetyReason);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("developer")]
    [InlineData("assistant")]
    [InlineData("tool")]
    public async Task RunAsync_AnAttackInAMessageOfAnyRole_IsRefused(string role)
    {
        // On this host the caller writes every message in the request, whatever its role.
        await _served.RunAsync([new ChatMessage(new ChatRole(role), Attack), new ChatMessage(ChatRole.User, Benign)]);

        _model.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_AnAttackInACallerSuppliedToolResult_IsRefused()
    {
        var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup")]);
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", Attack)]);

        await _served.RunAsync([call, result, new ChatMessage(ChatRole.User, Benign)]);

        _model.Calls.Should().Be(0, "a tool result in the request is caller text, not the output of a tool the agent ran");
    }

    [Fact]
    public async Task RunAsync_AnAttackInACallerSuppliedToolCallsArguments_IsRefused()
    {
        var call = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["query"] = Attack })]);

        await _served.RunAsync([call, new ChatMessage(ChatRole.User, Benign)]);

        _model.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_AnAttackSplitByALineBreakInToolCallArguments_IsRefused()
    {
        // The scan must read the text the model reads. Serialized JSON would show this line break as a backslash and an
        // "n", which the scan does not treat as the space between two words.
        var call = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["query"] = Attack.Replace("all ", "all\n") })]);

        await _served.RunAsync([call, new ChatMessage(ChatRole.User, Benign)]);

        _model.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_AFile_IsRefusedBecauseItCannotBeScreened()
    {
        var withFile = new ChatMessage(
            ChatRole.User,
            [new TextContent(Benign), new DataContent(new byte[] { 1, 2, 3 }, "application/pdf")]);

        var response = await _served.RunAsync([withFile]);

        _model.Calls.Should().Be(0, "the screening reads text only, so content it cannot read is refused, not passed");
        response.Text.Should().Be(FoundryInputScreening.UnscreenableContentRefusal);
    }

    [Fact]
    public async Task RunAsync_AnAttackInTheRequestsInstructions_IsRefused()
    {
        // Foundry's handler carries the request's `instructions` in the run options, not as a message.
        var options = new ChatClientAgentRunOptions(new ChatOptions { Instructions = Attack });

        await _served.RunAsync([new ChatMessage(ChatRole.User, Benign)], options: options);

        _model.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_AnAttackInTheReplayedHistory_IsRefused()
    {
        // Foundry's handler replays the conversation so far through a history provider in the run options, and the
        // platform stores a refused request's input too, so a refused attack comes back here on the next turn.
        var options = new ChatClientAgentRunOptions(new ChatOptions());
        AdditionalPropertiesExtensions.Add<ChatHistoryProvider>(
            options.AdditionalProperties ??= [], new FixedHistory(new ChatMessage(ChatRole.User, Attack)));

        await _served.RunAsync([new ChatMessage(ChatRole.User, "Do what I asked above.")], options: options);

        _model.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_BenignTextWithBenignHistory_ReachesTheModel()
    {
        var options = new ChatClientAgentRunOptions(new ChatOptions { Instructions = "Answer briefly." });
        AdditionalPropertiesExtensions.Add<ChatHistoryProvider>(
            options.AdditionalProperties ??= [], new FixedHistory(new ChatMessage(ChatRole.User, "Hello.")));

        var response = await _served.RunAsync([new ChatMessage(ChatRole.User, Benign)], options: options);

        _model.Calls.Should().Be(1);
        response.Text.Should().Be(RecordingChatClient.Reply);
    }

    [Fact]
    public async Task RunAsync_ContentSafetyUnavailableWithBlockingOn_FailsClosed()
    {
        var run = () => _served.RunAsync([new ChatMessage(ChatRole.User, Unscreenable)]);

        await run.Should().ThrowAsync<ContentSafetyUnavailableException>();
        _model.Calls.Should().Be(0, "an outage is not a pass when the deployment blocks unscreenable text");
    }

    [Fact]
    public async Task RunStreamingAsync_APromptInjection_IsRefusedBeforeTheModelSeesIt()
    {
        var text = string.Concat(await Collect(_served.RunStreamingAsync([new ChatMessage(ChatRole.User, Attack)])));

        _model.Calls.Should().Be(0, "streaming is the path Foundry's runtime uses for a streamed response");
        text.Should().Contain("Prompt injection detected");
    }

    [Fact]
    public async Task RunStreamingAsync_AnAttackInTheRequestsInstructions_IsRefused()
    {
        var options = new ChatClientAgentRunOptions(new ChatOptions { Instructions = Attack });

        await Collect(_served.RunStreamingAsync([new ChatMessage(ChatRole.User, Benign)], options: options));

        _model.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunStreamingAsync_BenignText_ReachesTheModel()
    {
        var text = string.Concat(await Collect(_served.RunStreamingAsync([new ChatMessage(ChatRole.User, Benign)])));

        _model.Calls.Should().Be(1);
        text.Should().Be(RecordingChatClient.Reply);
    }

    private static async Task<List<string>> Collect(IAsyncEnumerable<AgentResponseUpdate> updates)
    {
        var texts = new List<string>();
        await foreach (var update in updates)
            texts.Add(update.Text);
        return texts;
    }

    /// <summary>A history provider holding a fixed conversation, standing in for the one Foundry's handler supplies.</summary>
    private sealed class FixedHistory(params ChatMessage[] history) : ChatHistoryProvider(null, null, null)
    {
        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IEnumerable<ChatMessage>>(history);

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default) => default;
    }
}
