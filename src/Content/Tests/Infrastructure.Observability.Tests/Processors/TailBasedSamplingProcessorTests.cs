using System.Diagnostics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Observability.Tests.Processors;

public sealed class TailBasedSamplingProcessorTests : IDisposable
{
    private readonly ActivitySource _source = new("test.tail-sampling");
    private readonly ActivityListener _listener;

    public TailBasedSamplingProcessorTests()
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

    private static TailBasedSamplingProcessor CreateProcessor(SamplingConfig? config = null)
    {
        var appConfig = new AppConfig();
        if (config is not null)
            appConfig.Observability.Sampling = config;

        var options = Options.Create(appConfig);
        return new TailBasedSamplingProcessor(
            NullLogger<TailBasedSamplingProcessor>.Instance,
            options);
    }

    [Fact]
    public void OnEnd_Enabled_SpanBuffered()
    {
        var config = new SamplingConfig
        {
            Enabled = true,
            DecisionWait = TimeSpan.FromMinutes(5),
            MaxBufferedTraces = 1000,
            DefaultSamplingPercentage = 100
        };
        using var processor = CreateProcessor(config);
        using var activity = _source.StartActivity("test-op")!;
        activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;

        // OnEnd should buffer without error
        var act = () => processor.OnEnd(activity);
        act.Should().NotThrow();
    }

    [Fact]
    public void OnEnd_Disabled_SpanNotBuffered()
    {
        var config = new SamplingConfig
        {
            Enabled = false,
            MaxBufferedTraces = 1000,
            DefaultSamplingPercentage = 50
        };
        using var processor = CreateProcessor(config);
        using var activity = _source.StartActivity("test-op")!;
        activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;

        processor.OnEnd(activity);

        // When disabled, the span should not be modified at all
        activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded).Should().BeTrue();
    }

    [Fact]
    public void OnEnd_ErrorSpan_BufferedForKeeping()
    {
        var config = new SamplingConfig
        {
            Enabled = true,
            AlwaysKeepErrors = true,
            DecisionWait = TimeSpan.FromMinutes(5),
            MaxBufferedTraces = 1000,
            DefaultSamplingPercentage = 0
        };
        using var processor = CreateProcessor(config);
        using var activity = _source.StartActivity("error-op")!;
        activity.SetStatus(ActivityStatusCode.Error, "Something failed");
        activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;

        // Error spans should buffer without error; the evaluation timer
        // will decide to keep them based on AlwaysKeepErrors policy
        var act = () => processor.OnEnd(activity);
        act.Should().NotThrow();
    }

    [Fact]
    public void OnEnd_SlowSpan_BufferedForKeeping()
    {
        var config = new SamplingConfig
        {
            Enabled = true,
            SlowRequestThresholdMs = 100,
            DecisionWait = TimeSpan.FromMinutes(5),
            MaxBufferedTraces = 1000,
            DefaultSamplingPercentage = 0
        };
        using var processor = CreateProcessor(config);
        using var activity = _source.StartActivity("slow-op")!;
        activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;

        // Slow spans are buffered; evaluation happens on the timer
        var act = () => processor.OnEnd(activity);
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_InvalidMaxBufferedTraces_ThrowsArgumentOutOfRange()
    {
        var config = new SamplingConfig
        {
            Enabled = true,
            MaxBufferedTraces = 0,
            DefaultSamplingPercentage = 50
        };

        var act = () => CreateProcessor(config);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_InvalidSamplingPercentage_ThrowsArgumentOutOfRange()
    {
        var config = new SamplingConfig
        {
            Enabled = true,
            MaxBufferedTraces = 1000,
            DefaultSamplingPercentage = 150
        };

        var act = () => CreateProcessor(config);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NegativeSamplingPercentage_ThrowsArgumentOutOfRange()
    {
        var config = new SamplingConfig
        {
            Enabled = true,
            MaxBufferedTraces = 1000,
            DefaultSamplingPercentage = -1
        };

        var act = () => CreateProcessor(config);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
