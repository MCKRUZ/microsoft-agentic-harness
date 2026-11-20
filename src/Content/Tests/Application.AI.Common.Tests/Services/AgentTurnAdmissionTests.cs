using Application.AI.Common.Services;
using FluentAssertions;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// <see cref="AgentTurnAdmission"/> (#964): the caller's message is written once, only when the turn handler admits the
/// turn, never by a nested turn, and never after the transport has stopped waiting.
/// </summary>
public sealed class AgentTurnAdmissionTests
{
    [Fact]
    public async Task AdmitCurrentAsync_NothingWaiting_DoesNothing()
    {
        AgentTurnAdmission.Current = null;

        await AgentTurnAdmission.AdmitCurrentAsync(CancellationToken.None);

        AgentTurnAdmission.Current.Should().BeNull();
    }

    [Fact]
    public async Task AdmitCurrentAsync_RunsTheActionAndCloseReportsItAdmitted()
    {
        var writes = 0;
        var admission = new AgentTurnAdmission(_ => { writes++; return Task.CompletedTask; });

        await HandlerAsync(admission);

        writes.Should().Be(1);
        (await admission.CloseAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task AdmitCurrentAsync_ANestedTurnInsideTheHandler_DoesNotWriteTheMessageAgain()
    {
        // A delegated or sub-agent turn dispatched from inside this one runs the same handler on the same async flow.
        var writes = 0;
        var admission = new AgentTurnAdmission(_ => { writes++; return Task.CompletedTask; });
        AgentTurnAdmission.Current = admission;

        await HandlerWithNestedTurnAsync();

        writes.Should().Be(1);
    }

    [Fact]
    public async Task AdmitCurrentAsync_ClearsOnlyTheHandlersFlow_SoTheTransportStillHoldsItsAdmission()
    {
        var admission = new AgentTurnAdmission(_ => Task.CompletedTask);
        AgentTurnAdmission.Current = admission;

        await HandlerAsync();

        AgentTurnAdmission.Current.Should().BeSameAs(admission, "the transport restores it itself, in its finally");
    }

    [Fact]
    public async Task CloseAsync_BeforeAdmission_RefusesALateAdmissionAndWritesNothing()
    {
        // A timeout or disconnect stops the transport waiting while screening still runs; screening that passes after
        // that must not store the message the transport already reported as dropped.
        var writes = 0;
        var admission = new AgentTurnAdmission(_ => { writes++; return Task.CompletedTask; });

        (await admission.CloseAsync()).Should().BeFalse();
        var late = () => HandlerAsync(admission);

        await late.Should().ThrowAsync<OperationCanceledException>("the abandoned turn must stop before the model");
        writes.Should().Be(0);
    }

    [Fact]
    public async Task CloseAsync_WhileAdmissionIsWriting_WaitsAndReportsTheWrite()
    {
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new AgentTurnAdmission(async _ => { writing.SetResult(); await finish.Task; });

        var handler = HandlerAsync(admission);
        await writing.Task;
        var close = admission.CloseAsync();
        close.IsCompleted.Should().BeFalse("the answer must reflect what the store ends up holding");
        finish.SetResult();

        (await close).Should().BeTrue();
        await handler;
    }

    [Fact]
    public async Task AFailedWrite_PropagatesToTheHandlerAndIsNotAdmitted()
    {
        var admission = new AgentTurnAdmission(_ => Task.FromException(new IOException("disk")));

        var admit = () => HandlerAsync(admission);

        await admit.Should().ThrowAsync<IOException>("a turn whose message could not be stored does not run");
        (await admission.CloseAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_TheHandlerAdmits_ReportsTheResultAndAdmittedAndRestoresCurrent()
    {
        var outer = new AgentTurnAdmission(_ => Task.CompletedTask);
        AgentTurnAdmission.Current = outer;
        var writes = 0;
        var admission = new AgentTurnAdmission(_ => { writes++; return Task.CompletedTask; });

        var dispatched = await admission.DispatchAsync(async () => { await HandlerAsync(); return "answer"; });

        dispatched.Should().Be(new AdmittedDispatch<string>("answer", null, true));
        writes.Should().Be(1);
        AgentTurnAdmission.Current.Should().BeSameAs(outer, "the dispatch must not leak its admission into the caller's flow");
    }

    [Fact]
    public async Task DispatchAsync_TheDispatchThrowsBeforeAdmission_ReturnsTheFailureAndRefusesALateAdmission()
    {
        // The transport stopped waiting (a timeout) while screening was still running: the failure comes back as a value,
        // and the admission is already closed, so screening that passes later cannot store the message.
        var writes = 0;
        var admission = new AgentTurnAdmission(_ => { writes++; return Task.CompletedTask; });
        var timeout = new TimeoutException();

        var dispatched = await admission.DispatchAsync<string>(() => throw timeout);
        var late = async () => await admission.DispatchAsync(async () => { await HandlerAsync(); return "late"; });

        dispatched.Should().Be(new AdmittedDispatch<string>(null, timeout, false));
        (await late()).Failure.Should().BeOfType<OperationCanceledException>();
        writes.Should().Be(0);
    }

    // Stands in for ExecuteAgentTurnCommandHandler: an async method on the transport's flow.
    private static async Task HandlerAsync(AgentTurnAdmission? admission = null)
    {
        if (admission is not null)
            AgentTurnAdmission.Current = admission;
        await Task.Yield();
        await AgentTurnAdmission.AdmitCurrentAsync(CancellationToken.None);
    }

    private static async Task HandlerWithNestedTurnAsync()
    {
        await AgentTurnAdmission.AdmitCurrentAsync(CancellationToken.None);
        await HandlerAsync();
    }
}
