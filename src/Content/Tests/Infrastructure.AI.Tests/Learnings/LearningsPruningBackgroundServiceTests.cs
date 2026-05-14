using Application.AI.Common.Interfaces.Learnings;
using Domain.Common;
using Domain.Common.Config.AI.Learnings;
using FluentAssertions;
using Infrastructure.AI.Learnings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Learnings;

public sealed class LearningsPruningBackgroundServiceTests
{
    private readonly Mock<ILearningDecayService> _decayServiceMock;
    private readonly LearningsConfig _config;
    private readonly Mock<IOptionsMonitor<LearningsConfig>> _configMock;

    public LearningsPruningBackgroundServiceTests()
    {
        _decayServiceMock = new Mock<ILearningDecayService>();
        _decayServiceMock.Setup(d => d.PruneExpiredAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<int>.Success(0));
        _config = new LearningsConfig { PruneIntervalHours = 1 };
        _configMock = new Mock<IOptionsMonitor<LearningsConfig>>();
        _configMock.Setup(x => x.CurrentValue).Returns(_config);
    }

    private LearningsPruningBackgroundService CreateService() => new(
        _decayServiceMock.Object,
        _configMock.Object,
        Mock.Of<ILogger<LearningsPruningBackgroundService>>());

    [Fact]
    public async Task PruneNow_DelegatesToDecayService()
    {
        _decayServiceMock.Setup(d => d.PruneExpiredAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<int>.Success(5));

        var sut = CreateService();

        var result = await sut.PruneNowAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(5);
        _decayServiceMock.Verify(d => d.PruneExpiredAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsIntervalConfig()
    {
        _config.PruneIntervalHours = 24;
        var sut = CreateService();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await sut.StartAsync(cts.Token);

        try { await Task.Delay(200, CancellationToken.None); } catch { /* swallow */ }

        // With 24h interval and 100ms timeout, prune should NOT have been called
        _decayServiceMock.Verify(
            d => d.PruneExpiredAsync(It.IsAny<CancellationToken>()),
            Times.Never);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopsOnCancellation()
    {
        using var cts = new CancellationTokenSource();
        var sut = CreateService();

        await sut.StartAsync(cts.Token);

        cts.Cancel();

        var act = async () => await sut.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
