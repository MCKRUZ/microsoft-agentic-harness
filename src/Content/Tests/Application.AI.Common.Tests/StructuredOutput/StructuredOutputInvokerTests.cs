using Application.AI.Common.StructuredOutput;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.AI.Fakes;
using Xunit;

namespace Application.AI.Common.Tests.StructuredOutput;

/// <summary>
/// Proves <see cref="StructuredOutputInvoker"/>'s repair round-trip: attaches the schema, retries
/// once on malformed output, aborts early on an empty body, and never shows the model its own prior
/// bad attempt. Uses the real <see cref="RecordingChatClient"/> (via <see cref="RoleScript"/>) so
/// the invocation count and <c>ChatOptions.ResponseFormat</c> presence are asserted against what
/// actually reached the fake client, not a mocked expectation.
/// </summary>
public sealed class StructuredOutputInvokerTests
{
    private sealed record Plan(string Name, int StepCount);

    private static readonly StructuredOutputContract Contract =
        StructuredOutputSchema.Build<Plan>("test_plan");

    private static (StructuredOutputInvoker Invoker, RoleScript Script, ChatInvocationLog Log) CreateSut()
    {
        var log = new ChatInvocationLog();
        var script = new RoleScript();
        var invoker = new StructuredOutputInvoker(NullLogger<StructuredOutputInvoker>.Instance);
        return (invoker, script, log);
    }

    private static IChatClient ClientFor(RoleScript script, ChatInvocationLog log) =>
        new RecordingChatClient(agentId: "test", script, log);

    [Fact]
    public async Task InvokeAsync_ValidJsonFirstAttempt_ReturnsParsedSuccessWithoutRepair()
    {
        var (invoker, script, log) = CreateSut();
        script.Enqueue("""{"name":"build api","stepCount":3}""");
        var client = ClientFor(script, log);

        var result = await invoker.InvokeAsync<Plan>(
            client, Contract, [new ChatMessage(ChatRole.User, "go")], chatOptions: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new Plan("build api", 3));
        log.Invocations.Should().ContainSingle("no repair needed on a valid first attempt");
    }

    [Fact]
    public async Task InvokeAsync_MalformedThenValid_RepairsAndSucceeds()
    {
        var (invoker, script, log) = CreateSut();
        script.EnqueueMalformed().Enqueue("""{"name":"recovered","stepCount":1}""");
        var client = ClientFor(script, log);

        var result = await invoker.InvokeAsync<Plan>(
            client, Contract, [new ChatMessage(ChatRole.User, "go")], chatOptions: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new Plan("recovered", 1));
        log.Invocations.Should().HaveCount(2, "the repair round-trip took a second call");
    }

    [Fact]
    public async Task InvokeAsync_MalformedOnBothAttempts_ReturnsRepairFailed()
    {
        var (invoker, script, log) = CreateSut();
        script.EnqueueMalformed().EnqueueMalformed();
        var client = ClientFor(script, log);

        var result = await invoker.InvokeAsync<Plan>(
            client, Contract, [new ChatMessage(ChatRole.User, "go")], chatOptions: null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Outcome.Should().Be(StructuredOutcome.RepairFailed);
        log.Invocations.Should().HaveCount(2);
    }

    [Fact]
    public async Task InvokeAsync_NeverReachesTheUnreachableMalformedShape()
    {
        // THE CONTROL for a real defect a code-review fan-out caught: retryAddendum is set at the
        // end of attempt 0's iteration on ANY parse failure, so the post-loop branch can only ever
        // be reached after a repair was already attempted — there is no code path that fails
        // without one. StructuredOutcome intentionally has no separate "malformed, no repair
        // attempted" value because this loop can never produce it. This test scripts only ONE
        // malformed response (the queue then falls back to RoleScript's default, itself malformed)
        // and asserts two invocations happened and the outcome is RepairFailed — proving a repair
        // was actually attempted, not skipped.
        var (invoker, script, log) = CreateSut();
        script.EnqueueMalformed();
        var client = ClientFor(script, log);

        var result = await invoker.InvokeAsync<Plan>(
            client, Contract, [new ChatMessage(ChatRole.User, "go")], chatOptions: null, CancellationToken.None);

        log.Invocations.Should().HaveCount(2, "a repair is always attempted before giving up");
        result.Outcome.Should().Be(StructuredOutcome.RepairFailed);
    }

    [Fact]
    public async Task InvokeAsync_EmptyBody_AbortsWithoutARepairAttempt()
    {
        var (invoker, script, log) = CreateSut();
        script.EnqueueEmpty();
        var client = ClientFor(script, log);

        var result = await invoker.InvokeAsync<Plan>(
            client, Contract, [new ChatMessage(ChatRole.User, "go")], chatOptions: null, CancellationToken.None);

        result.Outcome.Should().Be(StructuredOutcome.EmptyResponse);
        // THE CONTROL: an empty body must not burn the repair attempt — a stricter format
        // instruction cannot fix "the model said nothing."
        log.Invocations.Should().ContainSingle("empty body aborts the retry budget early");
    }

    [Fact]
    public async Task InvokeAsync_ProviderThrows_ReturnsInvocationFailed()
    {
        var (invoker, script, log) = CreateSut();
        script.EnqueueThrow(new InvalidOperationException("provider unavailable"));
        var client = ClientFor(script, log);

        var result = await invoker.InvokeAsync<Plan>(
            client, Contract, [new ChatMessage(ChatRole.User, "go")], chatOptions: null, CancellationToken.None);

        result.Outcome.Should().Be(StructuredOutcome.InvocationFailed);
        result.ErrorMessage.Should().Contain("provider unavailable");
    }

    [Fact]
    public async Task InvokeAsync_AttachesResponseFormatOnEveryAttempt()
    {
        var (invoker, script, log) = CreateSut();
        script.EnqueueMalformed().Enqueue("""{"name":"x","stepCount":1}""");
        var client = ClientFor(script, log);

        await invoker.InvokeAsync<Plan>(
            client, Contract, [new ChatMessage(ChatRole.User, "go")], chatOptions: null, CancellationToken.None);

        log.Invocations.Should().HaveCount(2);
        log.Invocations.Should().OnlyContain(i => i.HadResponseFormat, "both the first attempt and the repair must carry the schema");
    }

    [Fact]
    public async Task InvokeAsync_RepairAttempt_DoesNotReplayTheModelsOwnPriorOutput()
    {
        // The retry appends a system-level addendum, never an assistant message echoing the bad
        // first attempt — verified via message count growth (system+user -> system+user+system).
        var (invoker, script, log) = CreateSut();
        script.EnqueueMalformed().Enqueue("""{"name":"x","stepCount":1}""");
        var client = ClientFor(script, log);

        await invoker.InvokeAsync<Plan>(
            client, Contract,
            [new ChatMessage(ChatRole.System, "sys"), new ChatMessage(ChatRole.User, "go")],
            chatOptions: null, CancellationToken.None);

        log.Invocations.Should().HaveCount(2);
        log.Invocations[0].MessageCount.Should().Be(2);
        log.Invocations[1].MessageCount.Should().Be(3, "the retry appends one addendum message, never the model's own prior output");
    }

    [Fact]
    public async Task InvokeAsync_ContractForWrongType_Throws()
    {
        var (invoker, script, log) = CreateSut();
        var client = ClientFor(script, log);
        var wrongContract = StructuredOutputSchema.Build<string>("wrong_type");

        var act = () => invoker.InvokeAsync<Plan>(
            client, wrongContract, [new ChatMessage(ChatRole.User, "go")], chatOptions: null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
