using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Remote;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Remote;

/// <summary>
/// Tests for <see cref="RemoteMemoryEnabledStartupWarning"/> — asserts the known-limitations
/// warning actually gets logged, so this disclosure control can't silently rot the way similarly
/// "shipped but never invoked" controls have in this repo's own history.
/// </summary>
public sealed class RemoteMemoryEnabledStartupWarningTests
{
    [Fact]
    public async Task StartAsync_LogsTheKnownLimitationsWarning()
    {
        var loggerMock = new Mock<ILogger<RemoteMemoryEnabledStartupWarning>>();
        var sut = new RemoteMemoryEnabledStartupWarning(loggerMock.Object);

        await sut.StartAsync(CancellationToken.None);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("RemoteMemory")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutThrowing()
    {
        var sut = new RemoteMemoryEnabledStartupWarning(Mock.Of<ILogger<RemoteMemoryEnabledStartupWarning>>());

        var act = () => sut.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
