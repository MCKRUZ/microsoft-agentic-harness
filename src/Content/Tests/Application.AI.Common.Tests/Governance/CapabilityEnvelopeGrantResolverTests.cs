using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Domain.AI.Bundles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Tests for the #657 cleanup of <see cref="CapabilityEnvelopeGrantResolver"/>: one definition of name
/// divergence, and a persistently broken tool logged at Error once rather than on every check.
/// </summary>
public sealed class CapabilityEnvelopeGrantResolverTests
{
    private const string BrokenToolKey = "broken_tool";

    private sealed class RecordingLogger : ILogger<CapabilityEnvelopeGrantResolver>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    private static CapabilityEnvelopeGrantResolver ResolverWithABrokenTool(RecordingLogger logger)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>(BrokenToolKey, (_, _) => throw new InvalidOperationException("dependency not wired"));
        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BrokenToolKey });
        return new CapabilityEnvelopeGrantResolver(lookup, logger);
    }

    [Fact]
    public void Grants_ToolWhoseConstructorKeepsThrowing_IsLoggedAtErrorOnceThenAtDebug()
    {
        var logger = new RecordingLogger();
        var resolver = ResolverWithABrokenTool(logger);
        var envelope = new CapabilityEnvelope { AllowedTools = [BrokenToolKey] };

        // Every tool-permission resolution under an armed envelope asks again, and the lookup deliberately
        // retries a construction failure each time.
        for (var i = 0; i < 3; i++)
            resolver.Grants(envelope, "some_other_name");

        logger.Levels.Should().Equal(LogLevel.Error, LogLevel.Debug, LogLevel.Debug);
    }

    [Theory]
    [InlineData("file_system", true, "read_file", true)]
    [InlineData("file_system", true, "File_System", false)]
    [InlineData("mcp_tool", false, "mcp_tool", false)]
    public void ToolNameFormsFor_DivergesOnlyForAResolvedNameThatDiffersBeyondCase(
        string name, bool resolved, string publishedName, bool expectedDiverges)
    {
        var forms = ToolNameForms.For(name, resolved, publishedName);

        forms.Diverges.Should().Be(expectedDiverges);
        forms.Forms.Should().HaveCount(expectedDiverges ? 2 : 1);
        forms.GroupingName.Should().Be(expectedDiverges ? publishedName : null);
    }
}
