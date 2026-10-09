using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;

#pragma warning disable MAAIW001

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// Pins which workflow output becomes <c>FinalOutput</c>. MAF's Magentic run emits the manager's
/// final answer as a <see cref="WorkflowOutputEvent"/> carrying a <c>List&lt;ChatMessage&gt;</c>
/// transcript, but also emits every participant's streaming reply through
/// <see cref="AgentResponseUpdateEvent"/> and <see cref="AgentResponseEvent"/> — both subclasses
/// of <see cref="WorkflowOutputEvent"/>, tagged <see cref="OutputTag.Intermediate"/>. A subscriber
/// that matches on the base type alone captures whichever arrived last, and stringifies the payload.
/// </summary>
[Collection("MagenticTraceCollection")]
public sealed class MagenticSubscriberOutputTests
{
    [Fact]
    public async Task ProcessEvent_TranscriptOutput_FinalOutputIsTheLastMessageText()
    {
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);
        var transcript = new List<ChatMessage>
        {
            new(ChatRole.User, "write the report"),
            new(ChatRole.Assistant, "the finished report")
        };

        await subscriber.ProcessEventAsync(new WorkflowOutputEvent(transcript, "manager"), default);

        subscriber.FinalOutput.Should().Be("the finished report");
    }

    [Fact]
    public async Task ProcessEvent_ParticipantUpdateAfterFinalOutput_DoesNotOverwriteIt()
    {
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);
        var transcript = new List<ChatMessage> { new(ChatRole.Assistant, "the finished report") };

        await subscriber.ProcessEventAsync(new WorkflowOutputEvent(transcript, "manager"), default);
        await subscriber.ProcessEventAsync(
            new AgentResponseUpdateEvent(
                "researcher",
                new AgentResponseUpdate(ChatRole.Assistant, "partial research"),
                OutputTag.Intermediate),
            default);

        subscriber.FinalOutput.Should().Be("the finished report");
    }

    [Fact]
    public async Task ProcessEvent_TranscriptWhoseLastMessageHasNoText_UsesTheLastMessageThatDoes()
    {
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);
        var transcript = new List<ChatMessage>
        {
            new(ChatRole.User, "write the report"),
            new(ChatRole.Assistant, "the finished report"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup")])
        };

        await subscriber.ProcessEventAsync(new WorkflowOutputEvent(transcript, "manager"), default);

        subscriber.FinalOutput.Should().Be("the finished report");
    }

    [Fact]
    public async Task ProcessEvent_TranscriptEndingInATextlessMessage_KeepsTheEarlierAnswer()
    {
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);

        await subscriber.ProcessEventAsync(
            new WorkflowOutputEvent(new List<ChatMessage> { new(ChatRole.Assistant, "the finished report") }, "manager"),
            default);
        // ChatMessage.Text is "" rather than null when a message has only tool-call content.
        await subscriber.ProcessEventAsync(
            new WorkflowOutputEvent(
                new List<ChatMessage> { new(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup")]) },
                "manager"),
            default);

        subscriber.FinalOutput.Should().Be("the finished report");
    }

    [Fact]
    public async Task ProcessEvent_ParticipantResponseOnly_LeavesFinalOutputUnset()
    {
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);

        await subscriber.ProcessEventAsync(
            new AgentResponseEvent(
                "researcher",
                new AgentResponse(new ChatMessage(ChatRole.Assistant, "partial research")),
                OutputTag.Intermediate),
            default);

        subscriber.FinalOutput.Should().BeNull(
            "a participant's reply is progress, not the manager's answer");
    }

    [Fact]
    public async Task ProcessEvent_IntermediateTaggedTranscript_LeavesFinalOutputUnset()
    {
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);
        var transcript = new List<ChatMessage> { new(ChatRole.Assistant, "not terminal") };

        await subscriber.ProcessEventAsync(
            new WorkflowOutputEvent(transcript, "researcher", OutputTag.Intermediate), default);

        subscriber.FinalOutput.Should().BeNull();
    }

    [Fact]
    public async Task ProcessEvent_UnrecognisedOutputPayload_DoesNotRecordATypeName()
    {
        var subscriber = MagenticTestHelpers.BuildSubscriber(out _, out _);

        await subscriber.ProcessEventAsync(new WorkflowOutputEvent(new object(), "manager"), default);

        subscriber.FinalOutput.Should().BeNull(
            "System.Object is a type name, and an answer that is a type name is worse than no answer");
    }
}

#pragma warning restore MAAIW001
