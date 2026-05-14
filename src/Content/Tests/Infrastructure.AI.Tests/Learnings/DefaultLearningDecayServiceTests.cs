using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config.AI.Learnings;
using FluentAssertions;
using Infrastructure.AI.Learnings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Learnings;

public sealed class DefaultLearningDecayServiceTests
{
    private readonly FakeTimeProvider _timeProvider;
    private readonly Mock<ILearningsStore> _storeMock;
    private readonly Mock<IOptionsMonitor<LearningsConfig>> _configMock;
    private readonly LearningsConfig _config;
    private readonly DefaultLearningDecayService _sut;

    public DefaultLearningDecayServiceTests()
    {
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 5, 11, 0, 0, 0, TimeSpan.Zero));
        _storeMock = new Mock<ILearningsStore>();
        _config = new LearningsConfig
        {
            VolatileShelfLifeDays = 7,
            StableShelfLifeDays = 180,
            PruneIntervalHours = 24,
            FeedbackAlpha = 0.25,
            DecayBiasAlpha = 0.25,
            BiasCorrection = true
        };
        _configMock = new Mock<IOptionsMonitor<LearningsConfig>>();
        _configMock.Setup(x => x.CurrentValue).Returns(_config);

        _sut = new DefaultLearningDecayService(
            _storeMock.Object,
            _configMock.Object,
            _timeProvider,
            Mock.Of<ILogger<DefaultLearningDecayService>>());
    }

    private LearningEntry CreateLearning(
        DecayClass decayClass,
        DateTimeOffset createdAt,
        DateTimeOffset? lastReinforcedAt = null,
        int updateCount = 0) => new()
    {
        LearningId = Guid.NewGuid(),
        Category = LearningCategory.DomainKnowledge,
        DecayClass = decayClass,
        Scope = new LearningScope { IsGlobal = true },
        Content = "Test learning",
        Source = new LearningSource
        {
            SourceType = LearningSourceType.HumanCorrection,
            SourceId = "test",
            SourceDescription = "Test source"
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "test",
            OriginTask = "test-task",
            OriginTimestamp = createdAt,
            Confidence = 1.0
        },
        CreatedAt = createdAt,
        LastReinforcedAt = lastReinforcedAt,
        UpdateCount = updateCount
    };

    [Fact]
    public async Task CalculateFreshness_VolatileDecay_7DayShelfLife()
    {
        var learning = CreateLearning(
            DecayClass.Volatile,
            _timeProvider.GetUtcNow().AddDays(-3.5));

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        freshness.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public async Task CalculateFreshness_StableDecay_180DayShelfLife()
    {
        var learning = CreateLearning(
            DecayClass.Stable,
            _timeProvider.GetUtcNow().AddDays(-90));

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        freshness.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public async Task CalculateFreshness_PermanentDecay_AlwaysReturnsOne()
    {
        var learning = CreateLearning(
            DecayClass.Permanent,
            _timeProvider.GetUtcNow().AddDays(-365));

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        freshness.Should().Be(1.0);
    }

    [Fact]
    public async Task CalculateFreshness_Expired_ReturnsZero()
    {
        var learning = CreateLearning(
            DecayClass.Volatile,
            _timeProvider.GetUtcNow().AddDays(-10));

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        freshness.Should().Be(0.0);
    }

    [Fact]
    public async Task CalculateFreshness_UsesLastReinforcedAt_WhenAvailable()
    {
        var learning = CreateLearning(
            DecayClass.Volatile,
            _timeProvider.GetUtcNow().AddDays(-100),
            lastReinforcedAt: _timeProvider.GetUtcNow().AddDays(-2));

        _config.BiasCorrection = false;

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        // age = 2 days, shelf = 7 → 1 - 2/7 ≈ 0.714
        freshness.Should().BeApproximately(1.0 - (2.0 / 7.0), 0.001);
    }

    [Fact]
    public async Task CalculateFreshness_FallsBackToCreatedAt_WhenNeverReinforced()
    {
        var learning = CreateLearning(
            DecayClass.Volatile,
            _timeProvider.GetUtcNow().AddDays(-5));

        _config.BiasCorrection = false;

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        // age = 5 days, shelf = 7 → 1 - 5/7 ≈ 0.286
        freshness.Should().BeApproximately(1.0 - (5.0 / 7.0), 0.001);
    }

    [Fact]
    public async Task CalculateFreshness_BiasCorrection_NewLearning_AdjustsUp()
    {
        var learning = CreateLearning(
            DecayClass.Stable,
            _timeProvider.GetUtcNow().AddDays(-90),
            updateCount: 1);

        _config.BiasCorrection = true;

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        // raw = 0.5, correction = 1/(1-(1-0.25)^1) = 4.0, 0.5*4.0 = 2.0, clamped to 1.0
        freshness.Should().Be(1.0);
    }

    [Fact]
    public async Task CalculateFreshness_BiasCorrection_Disabled_NoAdjustment()
    {
        var learning = CreateLearning(
            DecayClass.Stable,
            _timeProvider.GetUtcNow().AddDays(-90),
            updateCount: 1);

        _config.BiasCorrection = false;

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        freshness.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public async Task PruneExpired_RemovesExpiredVolatile()
    {
        var now = _timeProvider.GetUtcNow();
        var learnings = new List<LearningEntry>
        {
            CreateLearning(DecayClass.Volatile, now.AddDays(-10)), // expired
            CreateLearning(DecayClass.Volatile, now.AddDays(-8)),  // expired
            CreateLearning(DecayClass.Volatile, now.AddDays(-3))   // fresh
        };

        _storeMock.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(learnings));
        _storeMock.Setup(s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _sut.PruneExpiredAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);
        _storeMock.Verify(
            s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task PruneExpired_KeepsPermanent()
    {
        var now = _timeProvider.GetUtcNow();
        var learnings = new List<LearningEntry>
        {
            CreateLearning(DecayClass.Permanent, now.AddDays(-365)),
            CreateLearning(DecayClass.Permanent, now.AddDays(-1000))
        };

        _storeMock.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(learnings));

        var result = await _sut.PruneExpiredAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
        _storeMock.Verify(
            s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PruneExpired_KeepsFreshStable()
    {
        var now = _timeProvider.GetUtcNow();
        var learnings = new List<LearningEntry>
        {
            CreateLearning(DecayClass.Stable, now.AddDays(-30))
        };

        _storeMock.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(learnings));

        var result = await _sut.PruneExpiredAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
        _storeMock.Verify(
            s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PruneExpired_ReturnsCount()
    {
        var now = _timeProvider.GetUtcNow();
        var learnings = new List<LearningEntry>
        {
            CreateLearning(DecayClass.Volatile, now.AddDays(-10)), // expired
            CreateLearning(DecayClass.Stable, now.AddDays(-200)),  // expired
            CreateLearning(DecayClass.Volatile, now.AddDays(-8)),  // expired
            CreateLearning(DecayClass.Stable, now.AddDays(-30)),   // fresh
            CreateLearning(DecayClass.Permanent, now.AddDays(-999)) // permanent
        };

        _storeMock.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(learnings));
        _storeMock.Setup(s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await _sut.PruneExpiredAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(3);
    }

    [Fact]
    public async Task CalculateFreshness_ZeroShelfLife_ReturnsZero()
    {
        _config.VolatileShelfLifeDays = 0;
        var learning = CreateLearning(DecayClass.Volatile, _timeProvider.GetUtcNow().AddDays(-1));

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        freshness.Should().Be(0.0);
    }

    [Fact]
    public async Task CalculateFreshness_FutureTimestamp_ClampsToOne()
    {
        var learning = CreateLearning(
            DecayClass.Volatile,
            _timeProvider.GetUtcNow().AddDays(5));

        _config.BiasCorrection = false;

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        freshness.Should().Be(1.0);
    }

    [Fact]
    public async Task CalculateFreshness_ExactBoundary_ReturnsZero()
    {
        var learning = CreateLearning(
            DecayClass.Volatile,
            _timeProvider.GetUtcNow().AddDays(-7));

        _config.BiasCorrection = false;

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        freshness.Should().Be(0.0);
    }

    [Fact]
    public async Task CalculateFreshness_BiasCorrection_UpdateCountZero_NoCorrection()
    {
        var learning = CreateLearning(
            DecayClass.Stable,
            _timeProvider.GetUtcNow().AddDays(-90),
            updateCount: 0);

        _config.BiasCorrection = true;

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        freshness.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public async Task CalculateFreshness_BiasCorrection_UpdateCountFive_NoCorrection()
    {
        var learning = CreateLearning(
            DecayClass.Stable,
            _timeProvider.GetUtcNow().AddDays(-90),
            updateCount: 5);

        _config.BiasCorrection = true;

        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);

        freshness.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public async Task PruneExpired_SearchAllScopes_NullScope()
    {
        _storeMock.Setup(s => s.SearchAsync(It.Is<LearningSearchCriteria>(c => c.Scope == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(new List<LearningEntry>()));

        var result = await _sut.PruneExpiredAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _storeMock.Verify(s => s.SearchAsync(It.Is<LearningSearchCriteria>(c => c.Scope == null), It.IsAny<CancellationToken>()), Times.Once);
    }
}
