using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests for <see cref="ToolRiskClassifier"/> — resolving a tool's declared risk from keyed DI,
/// with a fail-safe default for names that do not resolve.
/// </summary>
public sealed class ToolRiskClassifierTests
{
    private static IToolRiskClassifier CreateClassifier(params ITool[] tools)
    {
        var services = new ServiceCollection();
        foreach (var tool in tools)
            services.AddKeyedSingleton<ITool>(tool.Name, tool);

        var toolNames = new HashSet<string>(tools.Select(t => t.Name), StringComparer.Ordinal);
        var lookup = new FirstPartyToolLookup(services.BuildServiceProvider(), toolNames);

        return new ToolRiskClassifier(lookup, NullLogger<ToolRiskClassifier>.Instance);
    }

    [Fact]
    public void Classify_KnownTool_ReturnsDeclaredRadiusAndReadOnly()
    {
        var sut = CreateClassifier(new FakeTool("deploy", BlastRadius.High, isReadOnly: false));

        var profile = sut.Classify("deploy");

        profile.Radius.Should().Be(BlastRadius.High);
        profile.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void Classify_ReadOnlyTool_ReflectsFlag()
    {
        var sut = CreateClassifier(new FakeTool("lookup", BlastRadius.Low, isReadOnly: true));

        var profile = sut.Classify("lookup");

        profile.Radius.Should().Be(BlastRadius.Low);
        profile.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Classify_UnknownTool_ReturnsFailSafeDefault()
    {
        var sut = CreateClassifier(new FakeTool("known", BlastRadius.Trivial, isReadOnly: true));

        var profile = sut.Classify("not-registered");

        profile.Should().Be(ToolRiskProfile.Default);
        profile.Radius.Should().Be(BlastRadius.Medium);
        profile.IsReadOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_BlankName_ReturnsDefault(string name)
    {
        var sut = CreateClassifier(new FakeTool("known", BlastRadius.High, isReadOnly: false));

        sut.Classify(name).Should().Be(ToolRiskProfile.Default);
    }

    [Fact]
    public void Classify_ToolConstructorThrows_ReturnsFailSafeDefaultInsteadOfPropagating()
    {
        // #627: this used FirstPartyToolLookup.Resolve, which propagates a keyed tool's constructor
        // exception instead of catching it — the same host-boot failure mode #612 fixed elsewhere.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("unbuildable", (_, _) =>
            throw new InvalidOperationException("dependency not registered in this host"));
        var lookup = new FirstPartyToolLookup(services.BuildServiceProvider(), new HashSet<string> { "unbuildable" });
        var logger = new Mock<ILogger<ToolRiskClassifier>>();
        var sut = new ToolRiskClassifier(lookup, logger.Object);

        var profile = sut.Classify("unbuildable");

        profile.Should().Be(ToolRiskProfile.Default);
        // The log is the point of the fix (#627 code-review) — a silent fallback with no error
        // signal is indistinguishable from an ordinary unrecognized tool, which defeats the whole
        // purpose of catching the construction failure instead of propagating it.
        LogsErrorMentioning(logger, "unbuildable").Should().BeTrue();
    }

    private static bool LogsErrorMentioning(Mock<ILogger<ToolRiskClassifier>> logger, string substring) =>
        logger.Invocations.Any(i =>
            i.Method.Name == nameof(ILogger.Log) &&
            i.Arguments.Count > 2 &&
            (LogLevel)i.Arguments[0]! == LogLevel.Error &&
            i.Arguments[2] is not null &&
            i.Arguments[2]!.ToString()!.Contains(substring, StringComparison.Ordinal));

    private sealed class FakeTool(string name, BlastRadius risk, bool isReadOnly) : ITool
    {
        public string Name => name;
        public string Description => "fake tool";
        public IReadOnlyList<string> SupportedOperations => [];
        public bool IsReadOnly => isReadOnly;
        public BlastRadius RiskTier => risk;

        public Task<ToolResult> ExecuteAsync(
            string operation,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used in classifier tests.");
    }
}
