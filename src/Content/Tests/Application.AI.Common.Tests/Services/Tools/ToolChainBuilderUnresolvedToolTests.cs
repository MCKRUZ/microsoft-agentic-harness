using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Pins #840: a tool name that no keyed-DI registration answers to is dropped from the chain, and
/// the drop is logged. Before, a skill that declared a tool which does not exist ran without it and
/// left no trace of why. The warning IS the behaviour under test: the tool set is unchanged.
/// </summary>
public sealed class ToolChainBuilderUnresolvedToolTests
{
    private sealed class CapturingLogger : ILogger<ToolChainBuilder>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public bool Logged(LogLevel level, string fragment) =>
            _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void BuildToolsByName_NameWithNoRegistration_IsDroppedAndWarnedAbout()
    {
        var logger = new CapturingLogger();
        var builder = new ToolChainBuilder(logger, new ServiceCollection().BuildServiceProvider());

        var tools = builder.BuildToolsByName(["no_such_tool"]);

        tools.Should().BeEmpty();
        logger.Logged(LogLevel.Warning, "'no_such_tool' is not registered").Should().BeTrue();
    }

    [Fact]
    public void BuildToolsByName_RegisteredName_DoesNotWarn()
    {
        var logger = new CapturingLogger();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("real_tool", Mock.Of<ITool>());
        var builder = new ToolChainBuilder(logger, services.BuildServiceProvider());

        builder.BuildToolsByName(["real_tool"]);

        logger.Logged(LogLevel.Warning, "is not registered").Should().BeFalse();
    }
}
