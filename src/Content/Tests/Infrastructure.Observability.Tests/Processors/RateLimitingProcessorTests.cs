using System.Diagnostics;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Observability.Tests.Processors;

public sealed class RateLimitingProcessorTests : IDisposable
{
    private readonly ActivitySource _source = new("test.rate-limiting");
    private readonly ActivityListener _listener;

    public RateLimitingProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    private static RateLimitingProcessor CreateProcessor(RateLimitingConfig? config = null)
    {
        var appConfig = new AppConfig();
        if (config is not null)
            appConfig.Observability.RateLimiting = config;

        var options = Options.Create(appConfig);
        return new RateLimitingProcessor(
            NullLogger<RateLimitingProcessor>.Instance,
            options);
    }

    [Fact]
    public void OnEnd_WithinRateLimit_SpanRemainsRecorded()
    {
        var config = new RateLimitingConfig
        {
            Enabled = true,
            SpansPerSecond = 100,
            BurstMultiplier = 2
        };
        var processor = CreateProcessor(config);
        using var activity = _source.StartActivity("test-op")!;
        activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;

        processor.OnEnd(activity);

        activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded).Should().BeTrue();
    }

    [Fact]
    public void OnEnd_ExceedsRateLimit_SpanDropped()
    {
        // Tiny bucket: 1 span/sec, burst multiplier 1 = 1 token total
        var config = new RateLimitingConfig
        {
            Enabled = true,
            SpansPerSecond = 1,
            BurstMultiplier = 1
        };
        var processor = CreateProcessor(config);

        // First span consumes the only token
        using var first = _source.StartActivity("first-op")!;
        first.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
        processor.OnEnd(first);
        first.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded).Should().BeTrue();

        // Second span should be dropped (no tokens left, no time to refill)
        using var second = _source.StartActivity("second-op")!;
        second.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
        processor.OnEnd(second);
        second.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded).Should().BeFalse();
    }

    [Fact]
    public void OnEnd_Disabled_AllSpansAllowed()
    {
        var config = new RateLimitingConfig
        {
            Enabled = false,
            SpansPerSecond = 1,
            BurstMultiplier = 1
        };
        var processor = CreateProcessor(config);

        // Even with a tiny bucket, disabled means all pass through
        for (var i = 0; i < 10; i++)
        {
            using var activity = _source.StartActivity($"op-{i}")!;
            activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
            processor.OnEnd(activity);
            activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded).Should().BeTrue(
                $"span {i} should not be dropped when rate limiting is disabled");
        }
    }

    [Fact]
    public void OnEnd_BurstCapacity_AllowsSpikesUpToLimit()
    {
        // 10 spans/sec with burst multiplier 3 = 30 token bucket
        var config = new RateLimitingConfig
        {
            Enabled = true,
            SpansPerSecond = 10,
            BurstMultiplier = 3
        };
        var processor = CreateProcessor(config);

        var recorded = 0;
        for (var i = 0; i < 30; i++)
        {
            using var activity = _source.StartActivity($"burst-{i}")!;
            activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
            processor.OnEnd(activity);
            if (activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded))
                recorded++;
        }

        // All 30 should pass (bucket starts full at maxTokens = 30)
        recorded.Should().Be(30);

        // The 31st should be dropped
        using var overflow = _source.StartActivity("overflow")!;
        overflow.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
        processor.OnEnd(overflow);
        overflow.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded).Should().BeFalse();
    }

    [Fact]
    public void Constructor_ZeroSpansPerSecond_ThrowsArgumentOutOfRange()
    {
        var config = new RateLimitingConfig
        {
            Enabled = true,
            SpansPerSecond = 0,
            BurstMultiplier = 2
        };

        var act = () => CreateProcessor(config);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ZeroBurstMultiplier_ThrowsArgumentOutOfRange()
    {
        var config = new RateLimitingConfig
        {
            Enabled = true,
            SpansPerSecond = 100,
            BurstMultiplier = 0
        };

        var act = () => CreateProcessor(config);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
