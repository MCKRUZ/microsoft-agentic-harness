using System.Diagnostics;
using Application.Common.MediatRBehaviors;
using Domain.Common.Telemetry;
using FluentAssertions;
using MediatR;
using Xunit;

namespace Application.Common.Tests.MediatRBehaviors;

public class RequestTracingBehaviorTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    private record TestRequest : IRequest<string>;

    public RequestTracingBehaviorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppSourceNames.AgenticHarnessMediatR,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Handle_SuccessfulRequest_CreatesActivityWithOkStatus()
    {
        var behavior = new RequestTracingBehavior<TestRequest, string>();

        var result = await behavior.Handle(
            new TestRequest(),
            () => Task.FromResult("traced"),
            CancellationToken.None);

        result.Should().Be("traced");
        _activities.Should().ContainSingle();
        var activity = _activities[0];
        activity.DisplayName.Should().Be(nameof(TestRequest));
        activity.Status.Should().Be(ActivityStatusCode.Ok);
        activity.GetTagItem("mediatr.request_type").Should().Be(typeof(TestRequest).FullName);
    }

    [Fact]
    public async Task Handle_FailedRequest_SetsErrorStatus()
    {
        var behavior = new RequestTracingBehavior<TestRequest, string>();
        var expectedException = new InvalidOperationException("boom");

        var act = () => behavior.Handle(
            new TestRequest(),
            () => throw expectedException,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        _activities.Should().ContainSingle();
        var activity = _activities[0];
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("boom");
        activity.Events.Should().Contain(e => e.Name == "exception");
    }

    [Fact]
    public async Task Handle_Request_TagsIncludeRequestType()
    {
        var behavior = new RequestTracingBehavior<TestRequest, string>();

        await behavior.Handle(
            new TestRequest(),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        _activities.Should().ContainSingle();
        _activities[0].GetTagItem("mediatr.request_type")
            .Should().Be(typeof(TestRequest).FullName);
    }
}
