using Application.Common.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Application.Common.Tests.Helpers;

public sealed class LastKnownGoodSettingTests
{
    [Fact]
    public void Current_ReadSucceeds_ReturnsTheFreshValueEveryTime()
    {
        var live = 5;
        var sut = new LastKnownGoodSetting<int>(() => live, seed: 1, new RecordingLogger(), "interval");

        sut.Current.Should().Be(5);
        live = 9;
        sut.Current.Should().Be(9, "a hot reload that validates must take effect on the very next read");
    }

    [Fact]
    public void Constructor_ReadsTheLiveValueOnce_SoLastGoodIsWhatTheHostStartedWith()
    {
        // The fallback is acted on (a retention window, a prune threshold). If a bad edit landed before the
        // first read, falling back to the shipped default instead of the operator's value would act on a
        // number nobody chose.
        var fail = false;
        var sut = new LastKnownGoodSetting<int>(
            () => fail ? throw new InvalidOperationException("bad") : 365, seed: 90, new RecordingLogger(), "retention");

        fail = true;

        sut.Current.Should().Be(365, "the value read at construction, not the seed");
    }

    [Fact]
    public void Current_ReadThrowsAfterASuccess_ReturnsTheLastGoodValueAndLogsTheFailure()
    {
        var logger = new RecordingLogger();
        var fail = false;
        var sut = new LastKnownGoodSetting<int>(
            () => fail ? throw new OptionsValidationException("x", typeof(object), ["bad edit"]) : 7,
            seed: 1, logger, "interval");
        sut.Current.Should().Be(7);

        fail = true;
        var value = sut.Current;

        value.Should().Be(7, "a mistyped live edit must leave the service running on what it had");
        logger.Errors.Should().ContainSingle().Which.Should().BeOfType<OptionsValidationException>();
    }

    [Fact]
    public void Current_ReadThrowsBeforeAnySuccess_ReturnsTheSeed()
    {
        var sut = new LastKnownGoodSetting<int>(
            () => throw new InvalidOperationException("never readable"), seed: 42, new RecordingLogger(), "interval");

        sut.Current.Should().Be(42);
    }

    [Fact]
    public void Current_ReadHealsAfterFailing_ReturnsTheNewValue()
    {
        var mode = 0;
        var sut = new LastKnownGoodSetting<int>(
            () => mode == 0 ? throw new InvalidOperationException("bad") : 3, seed: 1, new RecordingLogger(), "interval");

        sut.Current.Should().Be(1);
        mode = 1;
        sut.Current.Should().Be(3, "fixing the config must be picked up without a restart");
    }

    [Fact]
    public void Current_FailureLastsAcrossManyReads_LogsAtErrorOnlyOnceThenLogsTheRecovery()
    {
        var logger = new RecordingLogger();
        var fail = false;
        var sut = new LastKnownGoodSetting<int>(
            () => fail ? throw new InvalidOperationException("bad") : 7, seed: 1, logger, "interval");

        fail = true;
        for (var i = 0; i < 5; i++)
            _ = sut.Current;

        logger.Errors.Should().ContainSingle("a short-tick loop must not flood the log with one error per read");

        fail = false;
        _ = sut.Current;

        logger.Informations.Should().Be(1, "the recovery is worth one line");
    }

    [Fact]
    public void Current_ASecondFailureAfterRecovery_LogsAtErrorAgain()
    {
        var logger = new RecordingLogger();
        var fail = false;
        var sut = new LastKnownGoodSetting<int>(
            () => fail ? throw new InvalidOperationException("bad") : 7, seed: 1, logger, "interval");

        fail = true;
        _ = sut.Current;
        fail = false;
        _ = sut.Current;
        fail = true;
        _ = sut.Current;

        logger.Errors.Should().HaveCount(2, "each separate outage must be visible");
    }

    [Fact]
    public void Current_ReadIsCancelled_PropagatesRatherThanBeingMistakenForABadConfig()
    {
        var calls = 0;
        var sut = new LastKnownGoodSetting<int>(
            () => calls++ == 0 ? 1 : throw new OperationCanceledException(), seed: 1, new RecordingLogger(), "interval");

        var act = () => sut.Current;

        act.Should().Throw<OperationCanceledException>();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<Exception> Errors { get; } = [];

        public int Informations { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null)
                Errors.Add(exception);
            else if (logLevel == LogLevel.Information)
                Informations++;
        }
    }
}
