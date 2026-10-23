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
    [InlineData("AppConfig:AI:Rag:Documents:Retention")]
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
    public async Task TheDocumentAgeLimit_OverSharedStoresWithTheLocalLoadRecord_StopsTheHost()
    {
        // The default vector store is Azure AI Search, which other processes can write; the shipped load record is this
        // process's own file, so a removal could delete a document another host loaded yesterday.
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Rag:Documents:Retention:Enabled"] = "true",
        });

        var act = () => provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();

        act.Should().Throw<InvalidOperationException>().WithMessage("*LoadLedgerSharedByEveryWriter*");
    }

    [Fact]
    public async Task TheDocumentAgeLimit_WhenEveryWriterSharesTheLoadRecord_StartsTheHost()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Rag:Documents:Retention:Enabled"] = "true",
            ["AppConfig:AI:Rag:Documents:LoadLedgerSharedByEveryWriter"] = "true",
        });

        var act = () => provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();

        act.Should().NotThrow();
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
        app.AI.Rag.Documents.Retention.Enabled.Should().BeFalse("loaded documents are kept until removed unless an organisation sets a limit");
        app.AI.Rag.Documents.Retention.RetainFor.Should().Be(TimeSpan.FromDays(1095));
    }
}
