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
    public void Current_ReadIsCancelled_PropagatesRatherThanBeingMistakenForABadConfig()
    {
        var sut = new LastKnownGoodSetting<int>(
            () => throw new OperationCanceledException(), seed: 1, new RecordingLogger(), "interval");

        var act = () => sut.Current;

        act.Should().Throw<OperationCanceledException>();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null)
                Errors.Add(exception);
        }
    }
}
