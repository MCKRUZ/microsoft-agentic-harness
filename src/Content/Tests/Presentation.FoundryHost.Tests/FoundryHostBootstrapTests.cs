using Domain.AI.Agents;
using FluentAssertions;
using Presentation.FoundryHost;
using Xunit;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// Unit tests for <see cref="FoundryHostBootstrap"/> — the environment-to-config translation and
/// agent/skill selection that determine how the hosted container is wired and which agent it serves.
/// </summary>
public sealed class FoundryHostBootstrapTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] entries)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            map[key] = value;
        }

        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void BuildConfigOverrides_MapsFoundryEndpointAndDeployment_ToAppConfigKeys()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("FOUNDRY_PROJECT_ENDPOINT", "https://proj.services.ai.azure.com/api/projects/p1"),
            ("AZURE_AI_MODEL_DEPLOYMENT_NAME", "gpt-4o-mini")));

        overrides["AppConfig__AI__AIFoundry__ProjectEndpoint"]
            .Should().Be("https://proj.services.ai.azure.com/api/projects/p1");
        overrides["AppConfig__AI__AgentFramework__DefaultDeployment"].Should().Be("gpt-4o-mini");
    }

    [Fact]
    public void BuildConfigOverrides_WithAppInsights_EnablesAzureMonitorExporter()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=abc;IngestionEndpoint=https://x")));

        overrides["AppConfig__Observability__Exporters__AzureMonitor__ConnectionString"]
            .Should().Be("InstrumentationKey=abc;IngestionEndpoint=https://x");
        overrides["AppConfig__Observability__Exporters__AzureMonitor__Enabled"].Should().Be("true");
    }

    [Fact]
    public void BuildConfigOverrides_WithNoEnvironment_AppliesOnlyStandaloneRagDefault()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env());

        // No HOME, no Foundry inference vars, no external-store connections: the only override is
        // the in-process RAG fallback that keeps the container bootable without Azure AI Search.
        overrides.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new KeyValuePair<string, string>("AppConfig__AI__Rag__VectorStore__Provider", "faiss"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildConfigOverrides_IgnoresBlankSourceValues(string blank)
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("FOUNDRY_PROJECT_ENDPOINT", blank),
            ("APPLICATIONINSIGHTS_CONNECTION_STRING", blank),
            ("HOME", blank),
            ("GRAPH_PROVIDER", blank),
            ("AZURE_SEARCH_ENDPOINT", blank),
            ("REDIS_ENDPOINT", blank)));

        overrides.Should().NotContainKey("AppConfig__AI__AIFoundry__ProjectEndpoint");
        overrides.Should().NotContainKey("AppConfig__Observability__Exporters__AzureMonitor__ConnectionString");
        overrides.Should().NotContainKey("AppConfig__AI__Planner__DatabasePath");
        overrides.Should().NotContainKey("AppConfig__AI__Rag__GraphRag__GraphProvider");
        overrides.Should().NotContainKey("AppConfig__Cache__CacheType");
        // Blank search endpoint still yields the standalone in-process RAG default.
        overrides["AppConfig__AI__Rag__VectorStore__Provider"].Should().Be("faiss");
    }

    [Fact]
    public void BuildConfigOverrides_WithHome_RootsLocalStateUnderHome()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(("HOME", "/home/agent")));

        const string root = "/home/agent/agent-state";
        overrides["AppConfig__AI__Planner__DatabasePath"].Should().Be($"{root}/planner/planner.db");
        overrides["AppConfig__AI__DriftDetection__AuditPath"].Should().Be($"{root}/audit");
        overrides["AppConfig__AI__Governance__AuditStoragePath"].Should().Be($"{root}/governance");
        overrides["AppConfig__AI__Governance__Escalation__AuditStoragePath"].Should().Be($"{root}/escalations");
        overrides["AppConfig__AI__Changes__AuditStoragePath"].Should().Be($"{root}/changes");
        overrides["AppConfig__AI__Changes__EvidenceStoragePath"].Should().Be($"{root}/changes/evidence");
        overrides["AppConfig__AI__Egress__AuditStoragePath"].Should().Be($"{root}/egress");
        overrides["AppConfig__AI__ContextManagement__ToolResultStorage__StoragePath"].Should().Be(root);
        overrides["AppConfig__AI__Orchestration__Subagent__DelegationStoragePath"].Should().Be($"{root}/delegations");
        overrides["AppConfig__AI__Orchestration__Subagent__MailboxStoragePath"].Should().Be($"{root}/mailbox");
        overrides["AppConfig__AI__Rag__GraphDatabase__DataDirectory"].Should().Be($"{root}/graph");
        overrides["AppConfig__Logging__LogsBasePath"].Should().Be($"{root}/logs");
    }

    [Fact]
    public void BuildConfigOverrides_WithTrailingSlashHome_DoesNotDoubleSlash()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(("HOME", "/home/agent/")));

        overrides["AppConfig__AI__Planner__DatabasePath"].Should().Be("/home/agent/agent-state/planner/planner.db");
    }

    [Fact]
    public void BuildConfigOverrides_WithoutHome_DoesNotRootLocalState()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("FOUNDRY_PROJECT_ENDPOINT", "https://proj")));

        overrides.Should().NotContainKey("AppConfig__AI__Planner__DatabasePath");
        overrides.Should().NotContainKey("AppConfig__Logging__LogsBasePath");
    }

    [Fact]
    public void BuildConfigOverrides_WithGraphProviderAndConnection_PromotesAndEnablesGraphBackend()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("GRAPH_PROVIDER", "Neo4j"),
            ("GRAPH_CONNECTION_STRING", "bolt://user:pass@graph:7687")));

        // Provider is normalized to the lowercase DI key, and GraphRAG retrieval is switched on so
        // the wired backend is actually used (it defaults off).
        overrides["AppConfig__AI__Rag__GraphRag__GraphProvider"].Should().Be("neo4j");
        overrides["AppConfig__AI__Rag__GraphRag__ConnectionString"].Should().Be("bolt://user:pass@graph:7687");
        overrides["AppConfig__AI__Rag__GraphRag__Enabled"].Should().Be("true");
    }

    [Fact]
    public void BuildConfigOverrides_WithUnknownGraphProvider_ThrowsClearError()
    {
        var act = () => FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("GRAPH_PROVIDER", "cosmos_gremlin"),
            ("GRAPH_CONNECTION_STRING", "AccountEndpoint=https://x")));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*neo4j*postgresql*cosmos_gremlin*");
    }

    [Fact]
    public void BuildConfigOverrides_WithNativeVectorStoreEndpoint_DoesNotForceFaissFallback()
    {
        // Operator configured Azure AI Search via the native key but not the Provider key; the
        // standalone fallback must NOT clobber that endpoint with the in-process FAISS store.
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("AppConfig__AI__Rag__VectorStore__Endpoint", "https://svc.search.windows.net")));

        overrides.Should().NotContainKey("AppConfig__AI__Rag__VectorStore__Provider");
    }

    [Theory]
    [InlineData("neo4j", null)]
    [InlineData(null, "bolt://graph:7687")]
    public void BuildConfigOverrides_WithIncompleteGraphConfig_DoesNotPromote(string? provider, string? connection)
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("GRAPH_PROVIDER", provider),
            ("GRAPH_CONNECTION_STRING", connection)));

        overrides.Should().NotContainKey("AppConfig__AI__Rag__GraphRag__GraphProvider");
        overrides.Should().NotContainKey("AppConfig__AI__Rag__GraphRag__ConnectionString");
    }

    [Fact]
    public void BuildConfigOverrides_WithSearchEndpoint_PromotesVectorStoreWithOptionalCredentials()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("AZURE_SEARCH_ENDPOINT", "https://svc.search.windows.net"),
            ("AZURE_SEARCH_API_KEY", "secret"),
            ("AZURE_SEARCH_INDEX", "harness-chunks")));

        overrides["AppConfig__AI__Rag__VectorStore__Provider"].Should().Be("azure_ai_search");
        overrides["AppConfig__AI__Rag__VectorStore__Endpoint"].Should().Be("https://svc.search.windows.net");
        overrides["AppConfig__AI__Rag__VectorStore__ApiKey"].Should().Be("secret");
        overrides["AppConfig__AI__Rag__VectorStore__IndexName"].Should().Be("harness-chunks");
    }

    [Fact]
    public void BuildConfigOverrides_WithSearchEndpointOnly_OmitsCredentialKeysForManagedIdentity()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("AZURE_SEARCH_ENDPOINT", "https://svc.search.windows.net")));

        overrides["AppConfig__AI__Rag__VectorStore__Provider"].Should().Be("azure_ai_search");
        overrides.Should().NotContainKey("AppConfig__AI__Rag__VectorStore__ApiKey");
        overrides.Should().NotContainKey("AppConfig__AI__Rag__VectorStore__IndexName");
    }

    [Fact]
    public void BuildConfigOverrides_WithRedisEndpoint_PromotesDistributedCache()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(
            ("REDIS_ENDPOINT", "cache:6379"),
            ("REDIS_SECRET", "pw")));

        overrides["AppConfig__Cache__CacheType"].Should().Be("RedisCache");
        overrides["AppConfig__Cache__RedisClient__Endpoint"].Should().Be("cache:6379");
        overrides["AppConfig__Cache__RedisClient__Secret"].Should().Be("pw");
    }

    [Fact]
    public void BuildConfigOverrides_WithoutRedisEndpoint_LeavesCacheDefault()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Env(("HOME", "/home/agent")));

        overrides.Should().NotContainKey("AppConfig__Cache__CacheType");
    }

    [Fact]
    public void ResolveAgentId_WhenUnset_ReturnsDefault()
    {
        FoundryHostBootstrap.ResolveAgentId(Env()).Should().Be(FoundryHostBootstrap.DefaultAgentId);
        FoundryHostBootstrap.ResolveAgentId(Env(("FOUNDRY_AGENT_ID", "  "))).Should().Be("default");
    }

    [Fact]
    public void ResolveAgentId_WhenSet_ReturnsConfiguredId()
    {
        FoundryHostBootstrap.ResolveAgentId(Env(("FOUNDRY_AGENT_ID", "research"))).Should().Be("research");
    }

    [Fact]
    public void ResolveSkillIds_WithDeclaredSkills_ReturnsThem()
    {
        var definition = new AgentDefinition
        {
            Id = "orchestrator",
            Name = "Orchestrator",
            Skills = ["planner", "researcher"]
        };

        FoundryHostBootstrap.ResolveSkillIds(definition).Should().Equal("planner", "researcher");
    }

    [Fact]
    public void ResolveSkillIds_WithNoDeclaredSkills_FallsBackToAgentId()
    {
        var definition = new AgentDefinition { Id = "default", Name = "Default" };

        FoundryHostBootstrap.ResolveSkillIds(definition).Should().Equal("default");
    }
}
