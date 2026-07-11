using Application.AI.Common.CQRS.Bundles.GetBundleRun;
using Application.AI.Common.Interfaces.Bundles;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Bundles;

/// <summary>
/// Tests for <see cref="GetBundleRunQueryHandler"/>: the disabled gate, not-found (unknown and
/// wrong-handle), and the happy path — including that a run under a different handle is reported not found so
/// the poll surface leaks nothing.
/// </summary>
public sealed class GetBundleRunQueryHandlerTests
{
    private readonly Mock<IBundleRunJobStore> _jobStore = new();

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private GetBundleRunQueryHandler BuildSut(bool enabled = true)
    {
        var cfg = new AppConfig();
        cfg.AI.BundleExecution.Enabled = enabled;
        return new GetBundleRunQueryHandler(_jobStore.Object, new StaticOptionsMonitor<AppConfig>(cfg));
    }

    private static BundleRunRecord Record(string handle, string ownerId = "owner-1") => new()
    {
        JobId = "j1",
        Handle = handle,
        OwnerId = ownerId,
        AgentName = "agent-1",
        UserMessages = ["hello"],
        MaxTurns = 3,
        Envelope = new CapabilityEnvelope(),
        Status = BundleRunStatus.Succeeded,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static GetBundleRunQuery Query() => new() { Handle = "h1", JobId = "j1", OwnerId = "owner-1" };

    [Fact]
    public async Task Handle_WhenDisabled_ReturnsForbidden()
    {
        var result = await BuildSut(enabled: false).Handle(Query(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.Forbidden);
    }

    [Fact]
    public async Task Handle_WhenUnknownJobId_ReturnsNotFound()
    {
        _jobStore.Setup(j => j.Get("j1")).Returns((BundleRunRecord?)null);

        var result = await BuildSut().Handle(Query(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Handle_WhenRunBelongsToDifferentHandle_ReturnsNotFound()
    {
        _jobStore.Setup(j => j.Get("j1")).Returns(Record(handle: "someone-else"));

        var result = await BuildSut().Handle(Query(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Handle_WhenRunBelongsToDifferentCaller_ReturnsNotFound()
    {
        // Right handle + right job id, but the run was created by another owner: not found, no leak.
        _jobStore.Setup(j => j.Get("j1")).Returns(Record(handle: "h1", ownerId: "someone-else"));

        var result = await BuildSut().Handle(Query(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Handle_WhenRunMatchesHandleAndOwner_ReturnsRecord()
    {
        _jobStore.Setup(j => j.Get("j1")).Returns(Record(handle: "h1"));

        var result = await BuildSut().Handle(Query(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.JobId.Should().Be("j1");
        result.Value.Status.Should().Be(BundleRunStatus.Succeeded);
    }
}
