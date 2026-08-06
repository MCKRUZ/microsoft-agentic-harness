using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The startup disclosure that says whether conversations are bounded, and at what figure.
/// </summary>
/// <remarks>
/// The point of this type is that a deployment running without a ceiling cannot do so quietly, so these
/// tests assert on the <em>level</em> as much as the content: an operator scanning for warnings is the
/// reader it exists for, and an unbounded deployment reported at information level would be exactly the
/// silence it was written to end.
/// </remarks>
public sealed class ConversationBudgetStartupDisclosureTests
{
    [Fact]
    public async Task StartAsync_CeilingDisabled_WarnsThatConversationsAreUnbounded()
    {
        var (disclosure, logger) = Build(ceiling: 0);

        await disclosure.StartAsync(CancellationToken.None);

        var entry = logger.Single();
        entry.Level.Should().Be(LogLevel.Warning,
            "an unbounded deployment reported at information level is the silence this exists to end");
        entry.Message.Should().Contain("WITHOUT a lifetime token ceiling");
        entry.Message.Should().Contain("AppConfig:AI:AgentFramework:ConversationTokenBudget",
            "the reader has to be told which setting to change, not just that something is off");
    }

    [Fact]
    public async Task StartAsync_NegativeCeiling_WarnsThatConversationsAreUnbounded()
    {
        // The trackers go inert at anything not positive, not only at exactly zero. A disclosure that
        // tested for zero would stay quiet on the one value a fat-fingered edit is most likely to leave.
        var (disclosure, logger) = Build(ceiling: -1);

        await disclosure.StartAsync(CancellationToken.None);

        logger.Single().Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task StartAsync_CeilingConfigured_ReportsTheFigureWithoutWarning()
    {
        // The control. Without it, "warns when disabled" would also pass for a disclosure that warned
        // unconditionally — which would train operators to ignore it.
        var (disclosure, logger) = Build(ceiling: 250_000);

        await disclosure.StartAsync(CancellationToken.None);

        var entry = logger.Single();
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("250000", "an operator must be able to see the ceiling in force");
    }

    [Fact]
    public async Task StartAsync_StockConfiguration_ReportsBounded()
    {
        // Nothing configured at all — the shipped default, which is on. This is the test that would have
        // caught #256: every other test here states a ceiling, and a default nobody constructs untouched
        // is a default nothing measures.
        var (disclosure, logger) = Build(new AppConfig());

        await disclosure.StartAsync(CancellationToken.None);

        logger.Single().Level.Should().Be(LogLevel.Information,
            "a stock deployment is bounded, so it must not warn");
    }

    private static (ConversationBudgetStartupDisclosure Disclosure, RecordingLogger Logger) Build(int ceiling)
    {
        var config = new AppConfig();
        config.AI.AgentFramework.ConversationTokenBudget = ceiling;
        return Build(config);
    }

    private static (ConversationBudgetStartupDisclosure Disclosure, RecordingLogger Logger) Build(AppConfig config)
    {
        var logger = new RecordingLogger();
        var disclosure = new ConversationBudgetStartupDisclosure(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            logger);

        return (disclosure, logger);
    }

    /// <summary>
    /// Captures level and rendered message. Hand-rolled rather than mocked because every assertion here
    /// is about what an operator would actually read, which a rendered string states directly and a
    /// verification on a structured-logging call shape does not.
    /// </summary>
    private sealed class RecordingLogger : ILogger<ConversationBudgetStartupDisclosure>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public (LogLevel Level, string Message) Single()
        {
            _entries.Should().ContainSingle("the disclosure speaks once at startup, not per branch");
            return _entries[0];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
    }
}
