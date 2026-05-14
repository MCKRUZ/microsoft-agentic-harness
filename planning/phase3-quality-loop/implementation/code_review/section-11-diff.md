diff --git a/src/Content/Infrastructure/Infrastructure.AI/Learnings/DefaultLearningDecayService.cs b/src/Content/Infrastructure/Infrastructure.AI/Learnings/DefaultLearningDecayService.cs
new file mode 100644
index 0000000..94c9de0
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Learnings/DefaultLearningDecayService.cs
@@ -0,0 +1,90 @@
+using Application.AI.Common.Interfaces.Learnings;
+using Domain.AI.Learnings;
+using Domain.Common;
+using Domain.Common.Config.AI.Learnings;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.Learnings;
+
+/// <summary>
+/// Calculates temporal freshness scores for learnings based on their decay class
+/// and optionally applies EMA bias correction for low-sample learnings.
+/// </summary>
+public sealed class DefaultLearningDecayService : ILearningDecayService
+{
+    private readonly ILearningsStore _store;
+    private readonly IOptionsMonitor<LearningsConfig> _config;
+    private readonly TimeProvider _timeProvider;
+    private readonly ILogger<DefaultLearningDecayService> _logger;
+
+    public DefaultLearningDecayService(
+        ILearningsStore store,
+        IOptionsMonitor<LearningsConfig> config,
+        TimeProvider timeProvider,
+        ILogger<DefaultLearningDecayService> logger)
+    {
+        _store = store;
+        _config = config;
+        _timeProvider = timeProvider;
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public Task<double> CalculateFreshnessAsync(LearningEntry learning, CancellationToken ct)
+    {
+        if (learning.DecayClass == DecayClass.Permanent)
+            return Task.FromResult(1.0);
+
+        var config = _config.CurrentValue;
+        var shelfLifeDays = learning.DecayClass switch
+        {
+            DecayClass.Volatile => config.VolatileShelfLifeDays,
+            DecayClass.Stable => config.StableShelfLifeDays,
+            _ => config.StableShelfLifeDays
+        };
+
+        var referenceTime = learning.LastReinforcedAt ?? learning.CreatedAt;
+        var ageDays = (_timeProvider.GetUtcNow() - referenceTime).TotalDays;
+        var rawFreshness = Math.Max(0.0, 1.0 - (ageDays / shelfLifeDays));
+
+        if (config.BiasCorrection && learning.UpdateCount is > 0 and < 5)
+        {
+            var correctionFactor = 1.0 / (1.0 - Math.Pow(1.0 - config.FeedbackAlpha, learning.UpdateCount));
+            return Task.FromResult(Math.Clamp(rawFreshness * correctionFactor, 0.0, 1.0));
+        }
+
+        return Task.FromResult(rawFreshness);
+    }
+
+    /// <inheritdoc />
+    public async Task<Result<int>> PruneExpiredAsync(CancellationToken ct)
+    {
+        var criteria = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { IsGlobal = true }
+        };
+
+        var searchResult = await _store.SearchAsync(criteria, ct);
+        if (!searchResult.IsSuccess)
+            return Result<int>.Fail(searchResult.Errors.ToArray());
+
+        var prunedCount = 0;
+
+        foreach (var learning in searchResult.Value!)
+        {
+            if (learning.DecayClass == DecayClass.Permanent || learning.IsDeleted)
+                continue;
+
+            var freshness = await CalculateFreshnessAsync(learning, ct);
+            if (freshness <= 0.0)
+            {
+                await _store.SoftDeleteAsync(learning.LearningId, "Expired by decay service", ct);
+                prunedCount++;
+            }
+        }
+
+        _logger.LogInformation("Pruned {Count} expired learnings", prunedCount);
+        return Result<int>.Success(prunedCount);
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Learnings/LearningsPruningBackgroundService.cs b/src/Content/Infrastructure/Infrastructure.AI/Learnings/LearningsPruningBackgroundService.cs
new file mode 100644
index 0000000..e4c9dd7
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Learnings/LearningsPruningBackgroundService.cs
@@ -0,0 +1,60 @@
+using Application.AI.Common.Interfaces.Learnings;
+using Domain.Common;
+using Domain.Common.Config.AI.Learnings;
+using Microsoft.Extensions.Hosting;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.Learnings;
+
+/// <summary>
+/// Background service that periodically prunes expired learnings
+/// based on the configured interval.
+/// </summary>
+public sealed class LearningsPruningBackgroundService : BackgroundService
+{
+    private readonly ILearningDecayService _decayService;
+    private readonly IOptionsMonitor<LearningsConfig> _config;
+    private readonly ILogger<LearningsPruningBackgroundService> _logger;
+
+    public LearningsPruningBackgroundService(
+        ILearningDecayService decayService,
+        IOptionsMonitor<LearningsConfig> config,
+        ILogger<LearningsPruningBackgroundService> logger)
+    {
+        _decayService = decayService;
+        _config = config;
+        _logger = logger;
+    }
+
+    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
+    {
+        while (!stoppingToken.IsCancellationRequested)
+        {
+            var interval = TimeSpan.FromHours(_config.CurrentValue.PruneIntervalHours);
+            await Task.Delay(interval, stoppingToken);
+
+            try
+            {
+                var result = await _decayService.PruneExpiredAsync(stoppingToken);
+                if (result.IsSuccess)
+                    _logger.LogInformation("Pruning cycle complete: {Count} learnings removed", result.Value);
+                else
+                    _logger.LogWarning("Pruning cycle failed: {Errors}", string.Join(", ", result.Errors));
+            }
+            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
+            {
+                break;
+            }
+            catch (Exception ex)
+            {
+                _logger.LogError(ex, "Unhandled error during learnings pruning cycle");
+            }
+        }
+    }
+
+    /// <summary>
+    /// Exposed for testability — runs a single pruning cycle.
+    /// </summary>
+    public Task<Result<int>> PruneNowAsync(CancellationToken ct) => _decayService.PruneExpiredAsync(ct);
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Learnings/DefaultLearningDecayServiceTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Learnings/DefaultLearningDecayServiceTests.cs
new file mode 100644
index 0000000..2352fb4
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Learnings/DefaultLearningDecayServiceTests.cs
@@ -0,0 +1,275 @@
+using Application.AI.Common.Interfaces.Learnings;
+using Domain.AI.Learnings;
+using Domain.Common;
+using Domain.Common.Config.AI.Learnings;
+using FluentAssertions;
+using Infrastructure.AI.Learnings;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using Microsoft.Extensions.Time.Testing;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Learnings;
+
+public sealed class DefaultLearningDecayServiceTests
+{
+    private readonly FakeTimeProvider _timeProvider;
+    private readonly Mock<ILearningsStore> _storeMock;
+    private readonly Mock<IOptionsMonitor<LearningsConfig>> _configMock;
+    private readonly LearningsConfig _config;
+    private readonly DefaultLearningDecayService _sut;
+
+    public DefaultLearningDecayServiceTests()
+    {
+        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 5, 11, 0, 0, 0, TimeSpan.Zero));
+        _storeMock = new Mock<ILearningsStore>();
+        _config = new LearningsConfig
+        {
+            VolatileShelfLifeDays = 7,
+            StableShelfLifeDays = 180,
+            PruneIntervalHours = 24,
+            FeedbackAlpha = 0.25,
+            BiasCorrection = true
+        };
+        _configMock = new Mock<IOptionsMonitor<LearningsConfig>>();
+        _configMock.Setup(x => x.CurrentValue).Returns(_config);
+
+        _sut = new DefaultLearningDecayService(
+            _storeMock.Object,
+            _configMock.Object,
+            _timeProvider,
+            Mock.Of<ILogger<DefaultLearningDecayService>>());
+    }
+
+    private LearningEntry CreateLearning(
+        DecayClass decayClass,
+        DateTimeOffset createdAt,
+        DateTimeOffset? lastReinforcedAt = null,
+        int updateCount = 0) => new()
+    {
+        LearningId = Guid.NewGuid(),
+        Category = LearningCategory.DomainKnowledge,
+        DecayClass = decayClass,
+        Scope = new LearningScope { IsGlobal = true },
+        Content = "Test learning",
+        Source = new LearningSource
+        {
+            SourceType = LearningSourceType.HumanCorrection,
+            SourceId = "test",
+            SourceDescription = "Test source"
+        },
+        Provenance = new LearningProvenance
+        {
+            OriginPipeline = "test",
+            OriginTask = "test-task",
+            OriginTimestamp = createdAt,
+            Confidence = 1.0
+        },
+        CreatedAt = createdAt,
+        LastReinforcedAt = lastReinforcedAt,
+        UpdateCount = updateCount
+    };
+
+    [Fact]
+    public async Task CalculateFreshness_VolatileDecay_7DayShelfLife()
+    {
+        var learning = CreateLearning(
+            DecayClass.Volatile,
+            _timeProvider.GetUtcNow().AddDays(-3.5));
+
+        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);
+
+        freshness.Should().BeApproximately(0.5, 0.001);
+    }
+
+    [Fact]
+    public async Task CalculateFreshness_StableDecay_180DayShelfLife()
+    {
+        var learning = CreateLearning(
+            DecayClass.Stable,
+            _timeProvider.GetUtcNow().AddDays(-90));
+
+        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);
+
+        freshness.Should().BeApproximately(0.5, 0.001);
+    }
+
+    [Fact]
+    public async Task CalculateFreshness_PermanentDecay_AlwaysReturnsOne()
+    {
+        var learning = CreateLearning(
+            DecayClass.Permanent,
+            _timeProvider.GetUtcNow().AddDays(-365));
+
+        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);
+
+        freshness.Should().Be(1.0);
+    }
+
+    [Fact]
+    public async Task CalculateFreshness_Expired_ReturnsZero()
+    {
+        var learning = CreateLearning(
+            DecayClass.Volatile,
+            _timeProvider.GetUtcNow().AddDays(-10));
+
+        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);
+
+        freshness.Should().Be(0.0);
+    }
+
+    [Fact]
+    public async Task CalculateFreshness_UsesLastReinforcedAt_WhenAvailable()
+    {
+        var learning = CreateLearning(
+            DecayClass.Volatile,
+            _timeProvider.GetUtcNow().AddDays(-100),
+            lastReinforcedAt: _timeProvider.GetUtcNow().AddDays(-2));
+
+        _config.BiasCorrection = false;
+
+        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);
+
+        // age = 2 days, shelf = 7 → 1 - 2/7 ≈ 0.714
+        freshness.Should().BeApproximately(1.0 - (2.0 / 7.0), 0.001);
+    }
+
+    [Fact]
+    public async Task CalculateFreshness_FallsBackToCreatedAt_WhenNeverReinforced()
+    {
+        var learning = CreateLearning(
+            DecayClass.Volatile,
+            _timeProvider.GetUtcNow().AddDays(-5));
+
+        _config.BiasCorrection = false;
+
+        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);
+
+        // age = 5 days, shelf = 7 → 1 - 5/7 ≈ 0.286
+        freshness.Should().BeApproximately(1.0 - (5.0 / 7.0), 0.001);
+    }
+
+    [Fact]
+    public async Task CalculateFreshness_BiasCorrection_NewLearning_AdjustsUp()
+    {
+        var learning = CreateLearning(
+            DecayClass.Stable,
+            _timeProvider.GetUtcNow().AddDays(-90),
+            updateCount: 1);
+
+        _config.BiasCorrection = true;
+
+        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);
+
+        // raw = 0.5, correction = 1/(1-(1-0.25)^1) = 4.0, 0.5*4.0 = 2.0, clamped to 1.0
+        freshness.Should().Be(1.0);
+    }
+
+    [Fact]
+    public async Task CalculateFreshness_BiasCorrection_Disabled_NoAdjustment()
+    {
+        var learning = CreateLearning(
+            DecayClass.Stable,
+            _timeProvider.GetUtcNow().AddDays(-90),
+            updateCount: 1);
+
+        _config.BiasCorrection = false;
+
+        var freshness = await _sut.CalculateFreshnessAsync(learning, CancellationToken.None);
+
+        freshness.Should().BeApproximately(0.5, 0.001);
+    }
+
+    [Fact]
+    public async Task PruneExpired_RemovesExpiredVolatile()
+    {
+        var now = _timeProvider.GetUtcNow();
+        var learnings = new List<LearningEntry>
+        {
+            CreateLearning(DecayClass.Volatile, now.AddDays(-10)), // expired
+            CreateLearning(DecayClass.Volatile, now.AddDays(-8)),  // expired
+            CreateLearning(DecayClass.Volatile, now.AddDays(-3))   // fresh
+        };
+
+        _storeMock.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(learnings));
+        _storeMock.Setup(s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Result.Success());
+
+        var result = await _sut.PruneExpiredAsync(CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().Be(2);
+        _storeMock.Verify(
+            s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
+            Times.Exactly(2));
+    }
+
+    [Fact]
+    public async Task PruneExpired_KeepsPermanent()
+    {
+        var now = _timeProvider.GetUtcNow();
+        var learnings = new List<LearningEntry>
+        {
+            CreateLearning(DecayClass.Permanent, now.AddDays(-365)),
+            CreateLearning(DecayClass.Permanent, now.AddDays(-1000))
+        };
+
+        _storeMock.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(learnings));
+
+        var result = await _sut.PruneExpiredAsync(CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().Be(0);
+        _storeMock.Verify(
+            s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
+            Times.Never);
+    }
+
+    [Fact]
+    public async Task PruneExpired_KeepsFreshStable()
+    {
+        var now = _timeProvider.GetUtcNow();
+        var learnings = new List<LearningEntry>
+        {
+            CreateLearning(DecayClass.Stable, now.AddDays(-30))
+        };
+
+        _storeMock.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(learnings));
+
+        var result = await _sut.PruneExpiredAsync(CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().Be(0);
+        _storeMock.Verify(
+            s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
+            Times.Never);
+    }
+
+    [Fact]
+    public async Task PruneExpired_ReturnsCount()
+    {
+        var now = _timeProvider.GetUtcNow();
+        var learnings = new List<LearningEntry>
+        {
+            CreateLearning(DecayClass.Volatile, now.AddDays(-10)), // expired
+            CreateLearning(DecayClass.Stable, now.AddDays(-200)),  // expired
+            CreateLearning(DecayClass.Volatile, now.AddDays(-8)),  // expired
+            CreateLearning(DecayClass.Stable, now.AddDays(-30)),   // fresh
+            CreateLearning(DecayClass.Permanent, now.AddDays(-999)) // permanent
+        };
+
+        _storeMock.Setup(s => s.SearchAsync(It.IsAny<LearningSearchCriteria>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Result<IReadOnlyList<LearningEntry>>.Success(learnings));
+        _storeMock.Setup(s => s.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Result.Success());
+
+        var result = await _sut.PruneExpiredAsync(CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().Be(3);
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Learnings/LearningsPruningBackgroundServiceTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Learnings/LearningsPruningBackgroundServiceTests.cs
new file mode 100644
index 0000000..6c331c4
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Learnings/LearningsPruningBackgroundServiceTests.cs
@@ -0,0 +1,83 @@
+using Application.AI.Common.Interfaces.Learnings;
+using Domain.Common;
+using Domain.Common.Config.AI.Learnings;
+using FluentAssertions;
+using Infrastructure.AI.Learnings;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Learnings;
+
+public sealed class LearningsPruningBackgroundServiceTests
+{
+    private readonly Mock<ILearningDecayService> _decayServiceMock;
+    private readonly LearningsConfig _config;
+    private readonly Mock<IOptionsMonitor<LearningsConfig>> _configMock;
+
+    public LearningsPruningBackgroundServiceTests()
+    {
+        _decayServiceMock = new Mock<ILearningDecayService>();
+        _decayServiceMock.Setup(d => d.PruneExpiredAsync(It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Result<int>.Success(0));
+        _config = new LearningsConfig { PruneIntervalHours = 1 };
+        _configMock = new Mock<IOptionsMonitor<LearningsConfig>>();
+        _configMock.Setup(x => x.CurrentValue).Returns(_config);
+    }
+
+    private LearningsPruningBackgroundService CreateService() => new(
+        _decayServiceMock.Object,
+        _configMock.Object,
+        Mock.Of<ILogger<LearningsPruningBackgroundService>>());
+
+    [Fact]
+    public async Task PruneNow_DelegatesToDecayService()
+    {
+        _decayServiceMock.Setup(d => d.PruneExpiredAsync(It.IsAny<CancellationToken>()))
+            .ReturnsAsync(Result<int>.Success(5));
+
+        var sut = CreateService();
+
+        var result = await sut.PruneNowAsync(CancellationToken.None);
+
+        result.IsSuccess.Should().BeTrue();
+        result.Value.Should().Be(5);
+        _decayServiceMock.Verify(d => d.PruneExpiredAsync(It.IsAny<CancellationToken>()), Times.Once);
+    }
+
+    [Fact]
+    public async Task ExecuteAsync_RespectsIntervalConfig()
+    {
+        _config.PruneIntervalHours = 24;
+        var sut = CreateService();
+
+        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
+
+        await sut.StartAsync(cts.Token);
+
+        try { await Task.Delay(200, CancellationToken.None); } catch { /* swallow */ }
+
+        // With 24h interval and 100ms timeout, prune should NOT have been called
+        _decayServiceMock.Verify(
+            d => d.PruneExpiredAsync(It.IsAny<CancellationToken>()),
+            Times.Never);
+
+        await sut.StopAsync(CancellationToken.None);
+    }
+
+    [Fact]
+    public async Task StopsOnCancellation()
+    {
+        using var cts = new CancellationTokenSource();
+        var sut = CreateService();
+
+        await sut.StartAsync(cts.Token);
+
+        cts.Cancel();
+
+        var act = async () => await sut.StopAsync(CancellationToken.None);
+
+        await act.Should().NotThrowAsync();
+    }
+}
