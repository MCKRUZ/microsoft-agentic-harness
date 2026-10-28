using Domain.AI.Agents;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

    /// <summary>A process environment the tests can read and change.</summary>
    private static (Func<string, string?> Read, Action<string, string?> Write)
        FakeEnvironment(params (string Key, string? Value)[] entries)
    {
        var values = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        return (name => values.TryGetValue(name, out var v) ? v : null, (name, value) => values[name] = value);
    }

    [Fact]
    public void ApplyFoundryEnvironment_OptedOut_RemovesFoundrysConnectionStringAndMapsNoExport()
    {
        // The Foundry hosting layer exports to Azure Monitor whenever the variable is set, so turning only the
        // harness exporter off would still send this host's traces to Foundry's resource. Removing it stops both.
        var env = FakeEnvironment((FoundryHostBootstrap.AppInsightsVariable, "InstrumentationKey=abc"));

        var supplied = FoundryHostBootstrap.ApplyFoundryEnvironment(env.Read, env.Write, useHostConnectionString: false);

        supplied.Should().BeTrue();
        env.Read(FoundryHostBootstrap.AppInsightsVariable).Should().BeNull("nothing may read Foundry's connection string after the deployer opted out");
        env.Read("AppConfig__Observability__Exporters__AzureMonitor__Enabled").Should().BeNull();
        env.Read("AppConfig__Observability__Exporters__AzureMonitor__ConnectionString").Should().BeNull(
            "the deployer's own resource, if any, must not be replaced by Foundry's");
    }

    [Fact]
    public void ApplyFoundryEnvironment_NotOptedOut_KeepsFoundrysExportAndNeverOverwritesAnOperatorsValue()
    {
        var env = FakeEnvironment(
            (FoundryHostBootstrap.AppInsightsVariable, "InstrumentationKey=abc"),
            ("AppConfig__Observability__Exporters__AzureMonitor__Enabled", "false"));

        FoundryHostBootstrap.ApplyFoundryEnvironment(env.Read, env.Write, useHostConnectionString: true);

        env.Read(FoundryHostBootstrap.AppInsightsVariable).Should().Be("InstrumentationKey=abc");
        env.Read("AppConfig__Observability__Exporters__AzureMonitor__ConnectionString").Should().Be("InstrumentationKey=abc");
        env.Read("AppConfig__Observability__Exporters__AzureMonitor__Enabled").Should().Be("false",
            "a value an operator set with the native key is never overwritten");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("not-a-boolean", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void UsesHostConnectionString_ConfiguredValue_FalseOnlyForExplicitFalse(string? configured, bool uses) =>
        FoundryHostBootstrap.UsesHostConnectionString(configured).Should().Be(uses);

    [Fact]
    public void PrepareFoundryEnvironment_OptOutOnlyInALaterSource_HonoursItAndRemovesTheVariable()
    {
        // Key Vault and App Configuration are added after appsettings, User Secrets and the environment, so an opt-out
        // kept only there must win over an earlier "true". This proves the preparation honours the merged value it is
        // given; that Program.Main hands it the FULL configuration is guarded separately by
        // ProgramTelemetryOptOutWiringTests.Main_Always_ReadsTheOptOutFromTheFullConfiguration.
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection([new(FoundryHostBootstrap.EnableFromHostConnectionStringKey, "true")])
            .AddInMemoryCollection([new(FoundryHostBootstrap.EnableFromHostConnectionStringKey, "false")])
            .Build();
        var env = FakeEnvironment((FoundryHostBootstrap.AppInsightsVariable, "InstrumentationKey=abc"));

        var foundry = FoundryHostBootstrap.PrepareFoundryEnvironment(settings, env.Read, env.Write);

        foundry.HostSupplied.Should().BeTrue();
        foundry.UsedHostConnectionString.Should().BeFalse();
        env.Read(FoundryHostBootstrap.AppInsightsVariable).Should().BeNull(
            "the Foundry hosting layer exports whenever the variable is set");
        env.Read("AppConfig__Observability__Exporters__AzureMonitor__ConnectionString").Should().BeNull();
    }

    [Fact]
    public void PrepareFoundryEnvironment_NoSetting_UsesFoundrysConnectionString()
    {
        var env = FakeEnvironment((FoundryHostBootstrap.AppInsightsVariable, "InstrumentationKey=abc"));

        var foundry = FoundryHostBootstrap.PrepareFoundryEnvironment(
            new ConfigurationBuilder().Build(), env.Read, env.Write);

        foundry.Should().Be((true, true));
        env.Read(FoundryHostBootstrap.AppInsightsVariable).Should().Be("InstrumentationKey=abc");
        env.Read("AppConfig__Observability__Exporters__AzureMonitor__ConnectionString").Should().Be("InstrumentationKey=abc");
    }

    [Theory]
    [InlineData(true, "InstrumentationKey=abc", true)]
    [InlineData(true, "", false)]
    [InlineData(true, null, false)]
    [InlineData(false, "InstrumentationKey=abc", false)]
    public void HarnessExporterIsActive_EnabledAndConnectionString_MatchesTheExporterRegistration(
        bool enabled, string? connectionString, bool active) =>
        // ObservabilityTelemetryConfigurator adds the exporter only when both hold; Enabled alone sends nothing.
        FoundryHostBootstrap.HarnessExporterIsActive(
                new AzureMonitorExporterConfig { Enabled = enabled, ConnectionString = connectionString })
            .Should().Be(active);

    [Fact]
    public void DescribeTelemetryExport_Used_SaysBothLayersExportAndHowToStop()
    {
        var notices = FoundryHostBootstrap.DescribeTelemetryExport(
            hostSupplied: true, usedHostConnectionString: true, harnessExporterActive: true);

        notices.Should().ContainSingle().Which.Message.Should().Contain("hosting layer exports")
            .And.Contain("exporter is on").And.Contain(FoundryHostBootstrap.EnableFromHostConnectionStringKey);
    }

    [Fact]
    public void DescribeTelemetryExport_EnabledWithoutConnectionString_SaysTheExporterIsOff()
    {
        // The deployer set Enabled but gave no connection string, so the exporter is never registered.
        var active = FoundryHostBootstrap.HarnessExporterIsActive(new AzureMonitorExporterConfig { Enabled = true });

        var notices = FoundryHostBootstrap.DescribeTelemetryExport(
            hostSupplied: true, usedHostConnectionString: false, harnessExporterActive: active);

        notices.Should().ContainSingle().Which.Message.Should().Contain("exporter is off");
    }

    [Fact]
    public void DescribeTelemetryExport_OptedOut_SaysNothingGoesToFoundrysResource()
    {
        var notices = FoundryHostBootstrap.DescribeTelemetryExport(
            hostSupplied: true, usedHostConnectionString: false, harnessExporterActive: false);

        notices.Should().ContainSingle().Which.Message.Should().Contain("nothing is exported to Foundry's");
        notices.Should().OnlyContain(n => n.Level == LogLevel.Information);
    }

    [Fact]
    public void DescribeTelemetryExport_NoConnectionString_SaysNothing() =>
        FoundryHostBootstrap.DescribeTelemetryExport(false, true, false).Should().BeEmpty();

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
        overrides["AppConfig__AI__Erasure__AuditStoragePath"].Should().Be($"{root}/compliance");
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

    [Fact]
    public void ResolveSessionStoreRoot_WithHome_RootsUnderHomeCheckpoints()
    {
        var root = FoundryHostBootstrap.ResolveSessionStoreRoot(Env(("HOME", "/home/session")), "/app");

        root.Should().Be(Path.Combine("/home/session", ".checkpoints"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveSessionStoreRoot_WithoutUsableHome_FallsBackToTheWorkingDirectory(string? home)
    {
        var root = FoundryHostBootstrap.ResolveSessionStoreRoot(Env(("HOME", home)), "/app");

        root.Should().Be(Path.Combine("/app", ".checkpoints"));
    }

    [Fact]
    public void ModelContextProtocol_IsTheMajorVersionTheFoundryHostingPackageRequires()
    {
        // Microsoft.Agents.AI.Foundry.Hosting 1.24 requires ModelContextProtocol 2.x. Pinned solution-wide
        // so every project, not just this host, is built and tested against the version that ships.
        typeof(ModelContextProtocol.Client.McpClient).Assembly.GetName().Version!.Major.Should().Be(2);
    }
}
