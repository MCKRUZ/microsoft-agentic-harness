using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Tests.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The schedule around conversation expiry (#901): it runs on the interval with the configured period and batch, the
/// switch works, and a scoped expiry is resolved fresh for each sweep.
/// </summary>
/// <remarks>What expiry deletes is covered by <c>ConversationExpiryTests</c> and the composition test.</remarks>
public sealed class ConversationRetentionServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly ObservableFakeClock _clock = new(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));
    private readonly Mock<IConversationExpiry> _expiry = new();
    private readonly AppConfig _config = new();
    private int _scopesCreated;

    public ConversationRetentionServiceTests()
    {
        _config.AI.Conversations.Retention.SweepInterval = TimeSpan.FromHours(1);
        _config.AI.Conversations.Retention.IdlePeriod = TimeSpan.FromDays(200);
        _config.AI.Conversations.Retention.BatchSize = 42;
        _expiry.Setup(e => e.ExpireIdleAsync(It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
    }

    [Fact]
    public async Task EachTick_ExpiresWithTheConfiguredPeriodAndBatch_InAFreshScope()
    {
        using var service = CreateService();
        await StartAsync(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));
        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _expiry.Verify(e => e.ExpireIdleAsync(TimeSpan.FromDays(200), 42, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _scopesCreated.Should().Be(2, "the stores expiry erases through are scoped, so each sweep needs its own scope");
    }

    [Fact]
    public async Task Disabled_ExpiresNothing()
    {
        _config.AI.Conversations.Retention.Enabled = false;
        using var service = CreateService();
        await StartAsync(service);

        await AdvanceAndSettle(TimeSpan.FromHours(3));

        _expiry.Verify(e => e.ExpireIdleAsync(It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ConversationRetentionService CreateService()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            Interlocked.Increment(ref _scopesCreated);
            return _expiry.Object;
        });
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        return new ConversationRetentionService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            monitor.Object,
            _clock,
            NullLogger<ConversationRetentionService>.Instance);
    }

    private async Task StartAsync(ConversationRetentionService service)
    {
        var armed = _clock.NextTimer;
        await service.StartAsync(CancellationToken.None);
        await armed.WaitAsync(Patience);
    }

    private async Task AdvanceAndSettle(TimeSpan by)
    {
        var reArmed = _clock.NextTimer;
        _clock.Advance(by);
        await reArmed.WaitAsync(Patience);
    }
}
