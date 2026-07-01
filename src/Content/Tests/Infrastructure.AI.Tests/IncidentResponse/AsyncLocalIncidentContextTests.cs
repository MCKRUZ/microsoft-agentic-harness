using FluentAssertions;
using Infrastructure.AI.IncidentResponse;
using Xunit;

namespace Infrastructure.AI.Tests.IncidentResponse;

/// <summary>
/// Async-flow tests for <see cref="AsyncLocalIncidentContext"/>. Verifies the
/// AsyncLocal slot flows through await boundaries and isolates between
/// concurrent <c>Task.Run</c> branches.
/// </summary>
public sealed class AsyncLocalIncidentContextTests
{
    [Fact]
    public void CurrentIncidentType_DefaultsToNull()
    {
        var sut = new AsyncLocalIncidentContext();

        sut.CurrentIncidentType.Should().BeNull();
    }

    [Fact]
    public void Set_NullOrWhitespace_NormalisesToNull()
    {
        var sut = new AsyncLocalIncidentContext();

        sut.Set("DataExfiltrationSuspected");
        sut.CurrentIncidentType.Should().Be("DataExfiltrationSuspected");

        sut.Set("   ");
        sut.CurrentIncidentType.Should().BeNull();

        sut.Set("ProductionRollback");
        sut.CurrentIncidentType.Should().Be("ProductionRollback");

        sut.Set(null);
        sut.CurrentIncidentType.Should().BeNull();
    }

    [Fact]
    public async Task Set_FlowsThroughAsyncBoundary()
    {
        var sut = new AsyncLocalIncidentContext();
        sut.Set("DataExfiltrationSuspected");

        // Force an async boundary; AsyncLocal should carry the value.
        await Task.Yield();

        sut.CurrentIncidentType.Should().Be("DataExfiltrationSuspected");

        await Task.Delay(1);

        sut.CurrentIncidentType.Should().Be("DataExfiltrationSuspected");
    }

    [Fact]
    public async Task Set_ParallelTaskRunBranches_DoNotBleedIntoEachOther()
    {
        var sut = new AsyncLocalIncidentContext();
        // The outer context starts clean; each Task.Run inherits a copy of the
        // AsyncLocal slot at the moment of Task.Run, then mutates it locally.
        // Outer must remain null and the two branches must each see only their
        // own setting.

        var leftReady = new TaskCompletionSource();
        var rightReady = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var leftTask = Task.Run(async () =>
        {
            sut.Set("LeftIncident");
            leftReady.SetResult();
            await release.Task;
            return sut.CurrentIncidentType;
        });

        var rightTask = Task.Run(async () =>
        {
            sut.Set("RightIncident");
            rightReady.SetResult();
            await release.Task;
            return sut.CurrentIncidentType;
        });

        await leftReady.Task;
        await rightReady.Task;
        release.SetResult();

        var leftValue = await leftTask;
        var rightValue = await rightTask;

        leftValue.Should().Be("LeftIncident");
        rightValue.Should().Be("RightIncident");
        sut.CurrentIncidentType.Should().BeNull(
            because: "the outer async context was never written to; AsyncLocal copies down, not up");
    }

    [Fact]
    public async Task Set_ChildAsyncScope_InheritsValueFromCaller()
    {
        var sut = new AsyncLocalIncidentContext();
        sut.Set("OuterIncident");

        var inner = await Task.Run(() => sut.CurrentIncidentType);

        inner.Should().Be("OuterIncident",
            because: "Task.Run captures the AsyncLocal slot at the point of scheduling");
    }
}
