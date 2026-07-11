using Application.AI.Common.CQRS.Bundles.RegisterBundle;
using Application.AI.Common.Interfaces.Bundles;
using Domain.AI.Agents;
using Domain.AI.Bundles;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Bundles;

/// <summary>
/// Tests for <see cref="RegisterBundleCommandHandler"/>: the disabled gate, staging-failure pass-through, and
/// the happy path that registers a staged bundle and returns its handle.
/// </summary>
public sealed class RegisterBundleCommandHandlerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IBundleStagingService> _staging = new();
    private readonly Mock<IBundleHandleStore> _handleStore = new();

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private RegisterBundleCommandHandler BuildSut(bool enabled)
    {
        var cfg = new AppConfig();
        cfg.AI.BundleExecution.Enabled = enabled;
        cfg.AI.BundleExecution.HandleTtl = TimeSpan.FromMinutes(30);
        return new RegisterBundleCommandHandler(
            _staging.Object, _handleStore.Object,
            new StaticOptionsMonitor<AppConfig>(cfg), _time,
            NullLogger<RegisterBundleCommandHandler>.Instance);
    }

    private static StagedBundle Staged() => new()
    {
        BundleId = "b1",
        StagedRootDirectory = "/tmp/b1",
        Agent = new AgentDefinition { Id = "agent-1", Name = "Agent 1" }
    };

    private static RegisterBundleCommand Command() => new() { Archive = new MemoryStream([1, 2, 3]) };

    [Fact]
    public async Task Handle_WhenDisabled_ReturnsForbidden_AndDoesNotStage()
    {
        var result = await BuildSut(enabled: false).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        _staging.Verify(s => s.StageAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenStagingFails_PassesReasonThrough_AndDoesNotRegister()
    {
        _staging.Setup(s => s.StageAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<StagedBundle>.Fail("archive too large"));

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("archive too large");
        _handleStore.Verify(h => h.Register(It.IsAny<StagedBundle>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_RegistersAndReturnsHandleWithExpiry()
    {
        _staging.Setup(s => s.StageAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<StagedBundle>.Success(Staged()));
        _handleStore.Setup(h => h.Register(It.IsAny<StagedBundle>())).Returns("handle-1");

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Handle.Should().Be("handle-1");
        result.Value.ExpiresAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromMinutes(30));
        _handleStore.Verify(h => h.Register(It.Is<StagedBundle>(b => b.BundleId == "b1")), Times.Once);
    }
}
