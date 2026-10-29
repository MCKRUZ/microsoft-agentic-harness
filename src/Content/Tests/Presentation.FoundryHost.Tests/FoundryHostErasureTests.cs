using Domain.Common.Config;
using Domain.Common.Config.Cache;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Presentation.FoundryHost;
using Tests.Common;
using Xunit;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// FoundryHost serves no erasure or export endpoint, because Foundry routes only its protocol endpoints to the container
/// and each call reaches one session's sandbox (#1003). What it does instead is say so at startup, and name every store it
/// was bound with that deleting a Foundry session does not reach, since those hold records with no owner.
/// </summary>
public sealed class FoundryHostErasureTests
{
    private static AppConfig Standalone() => new()
    {
        AI = { Rag = { VectorStore = { Provider = "faiss" } } },
    };

    [Fact]
    public void Describe_Standalone_SaysHowToEraseAndExport_AndWarnsOfNothing()
    {
        var notices = FoundryHostErasure.Describe(Standalone());

        notices.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
        notices[0].Message.Should().Contain("no erasure or export endpoint").And.Contain("deleting the Foundry session")
            .And.Contain("session files API").And.Contain(FoundryHostErasure.GuideAnchor);
    }

    public static TheoryData<string, Action<AppConfig>> SharedStores => new()
    {
        { "GraphProvider = neo4j", c => c.AI.Rag.GraphRag.GraphProvider = "neo4j" },
        { "GraphProvider = postgresql", c => c.AI.Rag.GraphRag.GraphProvider = "postgresql" },
        { "GraphDatabase:Provider = neo4j", c => c.AI.Rag.GraphDatabase.Provider = "neo4j" },
        { "Azure AI Search", c =>
            {
                c.AI.Rag.VectorStore.Provider = "azure_ai_search";
                c.AI.Rag.VectorStore.Endpoint = "https://search.example";
            }
        },
        { "Redis", c => c.Cache.CacheType = CacheType.RedisCache },
    };

    [Theory]
    [MemberData(nameof(SharedStores))]
    public void Describe_AStoreSharedAcrossSessions_IsNamedInAWarning(string named, Action<AppConfig> share)
    {
        var config = Standalone();
        share(config);

        var warnings = FoundryHostErasure.Describe(config).Where(n => n.Level == LogLevel.Warning).ToList();

        warnings.Should().ContainSingle().Which.Message.Should().Contain(named)
            .And.Contain("no owner").And.Contain("cannot be erased or copied for one person");
    }

    [Fact]
    public void Describe_AnUnconfiguredAzureIndex_IsNotNamed()
    {
        // With no endpoint the index has never held anything, so it holds nobody's data to warn about.
        var config = Standalone();
        config.AI.Rag.VectorStore.Provider = "azure_ai_search";

        FoundryHostErasure.Describe(config).Should().OnlyContain(n => n.Level == LogLevel.Information);
    }

    [Fact]
    public void Describe_TheEmbeddedGraphDatabaseOrADisabledOne_IsNotNamed()
    {
        var config = Standalone();
        config.AI.Rag.GraphDatabase.Provider = "kuzu";
        FoundryHostErasure.Describe(config).Should().ContainSingle();

        config.AI.Rag.GraphDatabase.Provider = "neo4j";
        config.AI.Rag.GraphDatabase.Enabled = false;
        FoundryHostErasure.Describe(config).Should().ContainSingle();
    }

    [Fact]
    public void Main_LogsTheErasureNotice_FromTheBoundConfiguration()
    {
        // The notice is only worth anything if Program says it, after the configuration is bound.
        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(
            RepoRoot.Combine("src", "Content", "Presentation", "Presentation.FoundryHost", "Program.cs")));

        var describe = code.IndexOf("FoundryHostErasure.Describe(", StringComparison.Ordinal);
        var build = code.IndexOf(".BuildValidatedServiceProvider(", StringComparison.Ordinal);
        describe.Should().BeGreaterThan(build, "the notice reads the configuration the host bound");
        code[describe..].Should().MatchRegex(@"^FoundryHostErasure\.Describe\([^;]*\)\)\s*startupLog\.Log\(level,");
    }
}
