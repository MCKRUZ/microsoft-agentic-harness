using Domain.AI.Permissions;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Permissions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Permissions;

/// <summary>
/// Tests for <see cref="ConfigBasedRuleProvider"/> covering source type
/// and current behavior of returning empty rules.
/// </summary>
public sealed class ConfigBasedRuleProviderTests
{
    private readonly ConfigBasedRuleProvider _sut;

    public ConfigBasedRuleProviderTests()
    {
        var appConfig = new AppConfig();
        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);
        _sut = new ConfigBasedRuleProvider(options);
    }

    [Fact]
    public void Source_IsProjectSettings()
    {
        _sut.Source.Should().Be(PermissionRuleSource.ProjectSettings);
    }

    [Fact]
    public async Task GetRulesAsync_ReturnsEmptyList()
    {
        var rules = await _sut.GetRulesAsync("agent-1");

        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRulesAsync_DifferentAgentIds_AllReturnEmpty()
    {
        var rules1 = await _sut.GetRulesAsync("agent-1");
        var rules2 = await _sut.GetRulesAsync("agent-2");

        rules1.Should().BeEmpty();
        rules2.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRulesAsync_WithCancellation_Completes()
    {
        using var cts = new CancellationTokenSource();

        var rules = await _sut.GetRulesAsync("agent-1", cts.Token);

        rules.Should().BeEmpty();
    }
}
