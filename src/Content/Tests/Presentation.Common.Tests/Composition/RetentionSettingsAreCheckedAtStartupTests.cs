using Domain.Common.Config;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// The retention validators run in a real host (#901). A validator nothing invokes is the failure this repository has
/// shipped seven times; these resolve each bound section the way host start does.
/// </summary>
public sealed class RetentionSettingsAreCheckedAtStartupTests
{
    [Fact]
    public void AConversationIdlePeriodUnderADay_StopsTheHost()
    {
        using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:Retention:IdlePeriod"] = "01:00:00",
        });

        var act = () => provider.GetRequiredService<IOptions<ConversationRetentionConfig>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*IdlePeriod*");
    }

    [Theory]
    [InlineData("AppConfig:AI:PromptUsage:Retention")]
    [InlineData("AppConfig:MetaHarness:TraceRetention")]
    [InlineData("AppConfig:Observability:Retention")]
    [InlineData("AppConfig:Infrastructure:StateManagement:Retention")]
    [InlineData("AppConfig:AI:Changes:EvidenceRetention")]
    [InlineData("AppConfig:Logging:Retention")]
    public void ARetentionPeriodUnderADay_StopsTheHost(string section)
    {
        using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            [$"{section}:RetainFor"] = "01:00:00",
        });

        var act = () => provider.GetRequiredService<IOptionsMonitor<RetentionPeriodConfig>>().Get(section);

        act.Should().Throw<OptionsValidationException>().WithMessage("*RetainFor*");
    }

    [Fact]
    public void APersonalKnowledgePeriodUnderADay_StopsTheHost()
    {
        // Minutes meant as days would delete everyone's remembered knowledge within the hour.
        var act = () => CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Rag:GraphRag:PersonalKnowledgeRetention"] = "00:07:30",
        });

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*PersonalKnowledgeRetention*");
    }

    [Fact]
    public void TheDefaults_StartTheHost()
    {
        using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>());

        provider.GetRequiredService<IOptions<ConversationRetentionConfig>>().Value.IdlePeriod.Should().Be(TimeSpan.FromDays(365));
        provider.GetRequiredService<IOptionsMonitor<RetentionPeriodConfig>>().Get("AppConfig:AI:PromptUsage:Retention")
            .RetainFor.Should().Be(TimeSpan.FromDays(90));
        var app = provider.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
        app.MetaHarness.TraceRetention.RetainFor.Should().Be(TimeSpan.FromDays(30));
        app.Observability.Retention.RetainFor.Should().Be(TimeSpan.FromDays(90));
    }
}
