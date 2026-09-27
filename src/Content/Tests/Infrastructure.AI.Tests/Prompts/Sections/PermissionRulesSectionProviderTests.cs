using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Application.Core.Permissions;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts.Sections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
        var provider = Sut();

        provider.SectionType.Should().Be(SystemPromptSectionType.PermissionRules);
    }

    [Fact]
    public async Task GetSectionAsync_NoRules_ReturnsNull()
    {
        var emptyProvider = CreateRuleProvider(PermissionRuleSource.ProjectSettings);
        var provider = Sut(emptyProvider);

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
        var provider = Sut(ruleProvider);

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
        var provider = Sut(ruleProvider);

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
        var provider = Sut(ruleProvider);

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
        var provider = Sut(ruleProvider);

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
        var provider = Sut(ruleProvider);

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
        var provider = Sut(ruleProvider);

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

        var sut = Sut(provider1, provider2);

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

    // --- #652: the rule providers deliberately emit one rule per name-form for a first-party tool
    // whose DI registration key disagrees with its published name, so enforcement covers it either
    // way. Rendered verbatim that told the model two tools were restricted, one of them under a key
    // it can never invoke. Each rule of such a pair carries the published name they share, and the
    // summary groups on it — deliberately WITHOUT resolving tools here, which on the prompt path
    // would construct the host's entire tool set (see the type's remarks).

    [Fact]
    public async Task GetSectionAsync_KeyAndPublishedNameRules_RenderOnceUnderThePublishedName()
    {
        var ruleProvider = CreateRuleProvider(
            PermissionRuleSource.CapabilityEnvelope,
            DenyPairedForm("registered_key", "self_reported_name"),
            DenyPairedForm("self_reported_name", "self_reported_name"));

        var section = await Sut(ruleProvider).GetSectionAsync("agent-1");

        var denied = DeniedLines(section!.Content);
        denied.Should().ContainSingle("the two name-forms are one tool from the agent's perspective");
        denied[0].Should().Be("- self_reported_name", "the agent invokes by published name, never by DI key");
    }

    [Fact]
    public async Task GetSectionAsync_UntaggedKeyOnlyRule_RendersItsOwnPatternUnchanged()
    {
        // PluginPermissionRuleProvider's unverified-boundary fallback emits key-only rules with no
        // pairing tag, precisely so this summary never claims a restriction under a name the resolver
        // would not match. Such a rule must render as itself.
        var ruleProvider = CreateRuleProvider(PermissionRuleSource.PluginDeclaration, Deny("registered_key"));

        var section = await Sut(ruleProvider).GetSectionAsync("agent-1");

        DeniedLines(section!.Content).Should().ContainSingle().Which.Should().Be("- registered_key");
    }

    [Fact]
    public async Task GetSectionAsync_SameToolRestrictedByTwoProviders_RendersOnce()
    {
        // Duplication this fix collapses that no creation-time tag on a rule would have caught: two
        // independent providers each denying the same tool.
        var first = CreateRuleProvider(PermissionRuleSource.ProjectSettings, Deny("bash"));
        var second = CreateRuleProvider(PermissionRuleSource.PluginDeclaration, Deny("bash"));

        var section = await Sut(first, second).GetSectionAsync("agent-1");

        DeniedLines(section!.Content).Should().ContainSingle();
    }

    [Fact]
    public async Task GetSectionAsync_GlobPatterns_ArePassedThroughUnchanged()
    {
        // A glob is not a registration key; it must survive resolution untouched, and two different
        // globs must stay two lines.
        var ruleProvider = CreateRuleProvider(
            PermissionRuleSource.CapabilityEnvelope, Deny("*"), Deny("bash:*"));

        var section = await Sut(ruleProvider).GetSectionAsync("agent-1");

        DeniedLines(section!.Content).Should().BeEquivalentTo(["- *", "- bash:*"]);
    }

    [Fact]
    public async Task GetSectionAsync_SameToolDifferentOperations_KeepsBothLines()
    {
        // Grouping must not swallow a genuine second restriction on the same tool.
        var ruleProvider = CreateRuleProvider(
            PermissionRuleSource.ProjectSettings,
            new ToolPermissionRule("file_system", "read", PermissionBehaviorType.Deny,
                PermissionRuleSource.ProjectSettings, 10),
            new ToolPermissionRule("file_system", "write", PermissionBehaviorType.Deny,
                PermissionRuleSource.ProjectSettings, 10));

        var section = await Sut(ruleProvider).GetSectionAsync("agent-1");

        DeniedLines(section!.Content).Should().HaveCount(2);
    }

    [Fact]
    public async Task GetSectionAsync_SameToolAskedAndDenied_AppearsUnderBothHeadings()
    {
        // Grouping is within a behavior, never across it — a tool restricted both ways must still be
        // reported both ways.
        var ruleProvider = CreateRuleProvider(
            PermissionRuleSource.ProjectSettings,
            new ToolPermissionRule("file_system", null, PermissionBehaviorType.Ask,
                PermissionRuleSource.ProjectSettings, 10),
            Deny("file_system"));

        var section = await Sut(ruleProvider).GetSectionAsync("agent-1");

        section!.Content.Should().Contain("require approval before use");
        DeniedLines(section.Content).Should().ContainSingle().Which.Should().Be("- file_system");
    }

    // --- End-to-end over the REAL EnvelopePermissionRuleProvider. Every test above hand-builds its
    // rules, so none of them can tell whether a provider actually stamps the pairing: deleting
    // `PublishedToolName:` from an emission site left the whole suite green with the defect fully
    // restored (code-review finding). These drive the real provider into the real summary, so the
    // stamping is covered by something that fails when it is gone.

    /// <summary>
    /// A real <see cref="EnvelopePermissionRuleProvider"/> over a real
    /// <see cref="CapabilityEnvelopeGrantResolver"/>, with one first-party tool registered under
    /// <paramref name="key"/> that reports <paramref name="publishedName"/> as its own name.
    /// </summary>
    private static IPermissionRuleProvider RealEnvelopeProvider(string key, string publishedName)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>(key, (_, _) => Mock.Of<ITool>(t => t.Name == publishedName));
        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string> { key });

        return new EnvelopePermissionRuleProvider(
            NullLogger<EnvelopePermissionRuleProvider>.Instance,
            new CapabilityEnvelopeGrantResolver(
                lookup, NullLogger<CapabilityEnvelopeGrantResolver>.Instance));
    }

    [Fact]
    public async Task GetSectionAsync_RealProvider_GrantByKey_SummarisesTheToolOnce()
    {
        var provider = RealEnvelopeProvider("registered_key", "self_reported_name");
        var envelope = new CapabilityEnvelope
        {
            AllowedTools = ["registered_key"],
            AutonomyCeiling = AutonomyLevel.Supervised,
        };

        using (CapabilityEnvelopeAccessor.Begin(envelope))
        {
            var section = await Sut(provider).GetSectionAsync("bundle");

            ApprovalLines(section!.Content).Should().ContainSingle(
                "the key and published forms of one granted tool must summarise once")
                .Which.Should().Be("- self_reported_name");
        }
    }

    [Fact]
    public async Task GetSectionAsync_RealProvider_GrantedUnderBothNames_StillSummarisesOnce()
    {
        // The case the form-count heuristic got wrong: the published name is contributed by the first
        // grant entry, so the divergent second entry survives dedup holding a single form. Tagging
        // must key off divergence, not off how many forms survived.
        var provider = RealEnvelopeProvider("registered_key", "self_reported_name");
        var envelope = new CapabilityEnvelope
        {
            AllowedTools = ["self_reported_name", "registered_key"],
            AutonomyCeiling = AutonomyLevel.Supervised,
        };

        using (CapabilityEnvelopeAccessor.Begin(envelope))
        {
            var section = await Sut(provider).GetSectionAsync("bundle");

            ApprovalLines(section!.Content).Should().ContainSingle(
                "granting a tool under both of its names still restricts one tool")
                .Which.Should().Be("- self_reported_name");
        }
    }

    /// <summary>The bullet lines under the "require approval before use" heading.</summary>
    private static List<string> ApprovalLines(string content) =>
        BulletsUnder(content, "require approval before use:", stopAt: DeniedHeading);

    private static ToolPermissionRule Deny(string toolPattern) =>
        new(toolPattern, null, PermissionBehaviorType.Deny, PermissionRuleSource.CapabilityEnvelope, 1);

    /// <summary>
    /// One form of a name-pair: a rule matching <paramref name="toolPattern"/> that records the
    /// published name it shares with its sibling, exactly as the emitting providers stamp it.
    /// </summary>
    private static ToolPermissionRule DenyPairedForm(string toolPattern, string publishedToolName) =>
        new(toolPattern, null, PermissionBehaviorType.Deny, PermissionRuleSource.CapabilityEnvelope, 1,
            PublishedToolName: publishedToolName);

    private const string DeniedHeading = "The following tools are denied:";

    /// <summary>The bullet lines under the "denied" heading, which is always rendered last.</summary>
    private static List<string> DeniedLines(string content) =>
        BulletsUnder(content, DeniedHeading, stopAt: null);

    /// <summary>
    /// The bullet lines between <paramref name="heading"/> and <paramref name="stopAt"/> (or the end
    /// of the prompt when <paramref name="stopAt"/> is null).
    /// </summary>
    /// <remarks>
    /// Asserts the heading exists rather than trusting <see cref="string.IndexOf(string, StringComparison)"/>
    /// (code-review finding): on a miss it returns -1, and the resulting slice silently started a few
    /// characters into the whole prompt, so the helper returned the OTHER section's bullets. That made
    /// <c>GetSectionAsync_SameToolAskedAndDenied_AppearsUnderBothHeadings</c> pass under the exact
    /// regression it guards — a vanished denied section read as a present one. Shared by both callers
    /// so that hardening exists once (/simplify finding).
    /// </remarks>
    private static List<string> BulletsUnder(string content, string heading, string? stopAt)
    {
        var start = content.IndexOf(heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0,
            "the '{0}' heading must be present to have lines under it", heading);

        var rest = content[(start + heading.Length)..];

        if (stopAt is not null)
        {
            var end = rest.IndexOf(stopAt, StringComparison.Ordinal);
            if (end >= 0)
                rest = rest[..end];
        }

        return rest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith('-'))
            .ToList();
    }

    private static PermissionRulesSectionProvider Sut(params IPermissionRuleProvider[] ruleProviders) =>
        new(ruleProviders);

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
