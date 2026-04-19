using Application.AI.Common.Interfaces.Permissions;
using Domain.AI.Permissions;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts.Sections;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts.Sections;

/// <summary>
/// Tests for <see cref="PermissionRulesSectionProvider"/> covering rule formatting,
/// empty rule handling, and section metadata.
/// </summary>
public sealed class PermissionRulesSectionProviderTests
{
    [Fact]
    public void SectionType_IsPermissionRules()
    {
        var provider = new PermissionRulesSectionProvider(Enumerable.Empty<IPermissionRuleProvider>());

        provider.SectionType.Should().Be(SystemPromptSectionType.PermissionRules);
    }

    [Fact]
    public async Task GetSectionAsync_NoRules_ReturnsNull()
    {
        var emptyProvider = CreateRuleProvider(PermissionRuleSource.ProjectSettings);
        var provider = new PermissionRulesSectionProvider(new[] { emptyProvider });

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().BeNull();
    }

    [Fact]
    public async Task GetSectionAsync_AskRules_FormatsApprovalRequired()
    {
        var rule = new ToolPermissionRule(
            "file_system", "write:*",
            PermissionBehaviorType.Ask,
            PermissionRuleSource.ProjectSettings, 10, false);

        var ruleProvider = CreateRuleProvider(PermissionRuleSource.ProjectSettings, rule);
        var provider = new PermissionRulesSectionProvider(new[] { ruleProvider });

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("require approval before use");
        section.Content.Should().Contain("file_system (operation: write:*)");
    }

    [Fact]
    public async Task GetSectionAsync_DenyRules_FormatsDenied()
    {
        var rule = new ToolPermissionRule(
            "dangerous_tool", null,
            PermissionBehaviorType.Deny,
            PermissionRuleSource.ProjectSettings, 10, false);

        var ruleProvider = CreateRuleProvider(PermissionRuleSource.ProjectSettings, rule);
        var provider = new PermissionRulesSectionProvider(new[] { ruleProvider });

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("denied");
        section.Content.Should().Contain("dangerous_tool");
    }

    [Fact]
    public async Task GetSectionAsync_MixedRules_IncludesBothSections()
    {
        var askRule = new ToolPermissionRule(
            "file_system", null,
            PermissionBehaviorType.Ask,
            PermissionRuleSource.ProjectSettings, 10, false);

        var denyRule = new ToolPermissionRule(
            "exec", null,
            PermissionBehaviorType.Deny,
            PermissionRuleSource.ProjectSettings, 10, false);

        var ruleProvider = CreateRuleProvider(PermissionRuleSource.ProjectSettings, askRule, denyRule);
        var provider = new PermissionRulesSectionProvider(new[] { ruleProvider });

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("require approval");
        section.Content.Should().Contain("denied");
    }

    [Fact]
    public async Task GetSectionAsync_RuleWithoutOperation_OmitsOperationSuffix()
    {
        var rule = new ToolPermissionRule(
            "search", null,
            PermissionBehaviorType.Ask,
            PermissionRuleSource.ProjectSettings, 10, false);

        var ruleProvider = CreateRuleProvider(PermissionRuleSource.ProjectSettings, rule);
        var provider = new PermissionRulesSectionProvider(new[] { ruleProvider });

        var section = await provider.GetSectionAsync("agent-1");

        section!.Content.Should().Contain("- search");
        section.Content.Should().NotContain("(operation:");
    }

    [Fact]
    public async Task GetSectionAsync_IsCacheable()
    {
        var rule = new ToolPermissionRule(
            "tool", null,
            PermissionBehaviorType.Ask,
            PermissionRuleSource.ProjectSettings, 10, false);

        var ruleProvider = CreateRuleProvider(PermissionRuleSource.ProjectSettings, rule);
        var provider = new PermissionRulesSectionProvider(new[] { ruleProvider });

        var section = await provider.GetSectionAsync("agent-1");

        section!.IsCacheable.Should().BeTrue();
    }

    [Fact]
    public async Task GetSectionAsync_Priority_Is40()
    {
        var rule = new ToolPermissionRule(
            "tool", null,
            PermissionBehaviorType.Ask,
            PermissionRuleSource.ProjectSettings, 10, false);

        var ruleProvider = CreateRuleProvider(PermissionRuleSource.ProjectSettings, rule);
        var provider = new PermissionRulesSectionProvider(new[] { ruleProvider });

        var section = await provider.GetSectionAsync("agent-1");

        section!.Priority.Should().Be(40);
    }

    [Fact]
    public async Task GetSectionAsync_MultipleProviders_AggregatesRules()
    {
        var provider1 = CreateRuleProvider(PermissionRuleSource.ProjectSettings,
            new ToolPermissionRule("tool1", null, PermissionBehaviorType.Ask,
                PermissionRuleSource.ProjectSettings, 10, false));

        var provider2 = CreateRuleProvider(PermissionRuleSource.AgentManifest,
            new ToolPermissionRule("tool2", null, PermissionBehaviorType.Deny,
                PermissionRuleSource.AgentManifest, 20, false));

        var sut = new PermissionRulesSectionProvider(new[] { provider1, provider2 });

        var section = await sut.GetSectionAsync("agent-1");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("tool1");
        section.Content.Should().Contain("tool2");
    }

    [Fact]
    public void Constructor_NullProviders_Throws()
    {
        var act = () => new PermissionRulesSectionProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static IPermissionRuleProvider CreateRuleProvider(
        PermissionRuleSource source,
        params ToolPermissionRule[] rules)
    {
        var mock = new Mock<IPermissionRuleProvider>();
        mock.Setup(x => x.Source).Returns(source);
        mock.Setup(x => x.GetRulesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ToolPermissionRule>)rules);
        return mock.Object;
    }
}
