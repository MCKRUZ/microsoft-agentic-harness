using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

        return new ToolRiskClassifier(services.BuildServiceProvider());
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
