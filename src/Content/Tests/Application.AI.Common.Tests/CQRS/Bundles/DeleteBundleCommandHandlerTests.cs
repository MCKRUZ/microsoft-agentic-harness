using Application.AI.Common.CQRS.Bundles.DeleteBundle;
using Application.AI.Common.Interfaces.Bundles;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Bundles;

/// <summary>
/// Tests for <see cref="DeleteBundleCommandHandler"/>: the disabled gate and the idempotent removal path.
/// </summary>
public sealed class DeleteBundleCommandHandlerTests
{
    private readonly Mock<IBundleHandleStore> _handleStore = new();

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private DeleteBundleCommandHandler BuildSut(bool enabled)
    {
        var cfg = new AppConfig();
        cfg.AI.BundleExecution.Enabled = enabled;
        return new DeleteBundleCommandHandler(
            _handleStore.Object, new StaticOptionsMonitor<AppConfig>(cfg),
            NullLogger<DeleteBundleCommandHandler>.Instance);
    }

    private static DeleteBundleCommand Command(string handle = "h1") => new() { Handle = handle, OwnerId = "owner-1" };

    [Fact]
    public async Task Handle_WhenDisabled_ReturnsForbidden_AndDoesNotRemove()
    {
        var result = await BuildSut(enabled: false).Handle(Command(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        _handleStore.Verify(h => h.Remove(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenOwnedByCaller_RemovesAndReportsTrue()
    {
        _handleStore.Setup(h => h.GetOwner("h1")).Returns("owner-1");
        _handleStore.Setup(h => h.Remove("h1")).Returns(true);

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenAbsent_StillSucceeds_ReportsFalse_Idempotent()
    {
        _handleStore.Setup(h => h.GetOwner("gone")).Returns((string?)null);

        var result = await BuildSut(enabled: true).Handle(Command("gone"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse();
        _handleStore.Verify(h => h.Remove(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenOwnedByAnotherCaller_IsNoOp_DoesNotRemove()
    {
        _handleStore.Setup(h => h.GetOwner("h1")).Returns("someone-else");

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse();
        _handleStore.Verify(h => h.Remove(It.IsAny<string>()), Times.Never);
    }
}
