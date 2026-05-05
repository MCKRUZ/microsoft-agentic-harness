# Telemetry Verification Suite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a 4-layer test suite that verifies metrics flow from a real chat action all the way through to Prometheus, catching any breakage in the pipeline.

**Architecture:** Each layer is independently runnable. Layers 1-3 run without Docker (PR gate). Layer 4 requires Docker (nightly/manual). A shared `MetricNamingContract` class encodes the canonical instrument→Prometheus transform used by Layers 2, 3, and 4.

**Tech Stack:** xUnit, FluentAssertions, Testcontainers.DotNet, YamlDotNet, Microsoft.Playwright, Microsoft.AspNetCore.Mvc.Testing, Moq

---

## File Structure

| File | Responsibility |
|------|---------------|
| `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/MetricsIntegrationTests.cs` | Layer 1: Real handler → metrics emission |
| `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/CollectorContractTests.cs` | Layer 2: Naming transform validation |
| `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/DashboardContractTests.cs` | Layer 3: PromQL → valid metric names |
| `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/MetricsE2ETests.cs` | Layer 4: Full pipeline with Testcontainers |
| `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Contracts/MetricNamingContract.cs` | Shared: Instrument registry + Prometheus transform |
| `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Contracts/InstrumentDefinition.cs` | Shared: Instrument metadata record |
| `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Fixtures/MetricsIntegrationFactory.cs` | Layer 1: TestWebApplicationFactory with mock AI client |
| `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Fixtures/PrometheusFixture.cs` | Layer 4: Testcontainers orchestration |

---

## Task 1: Add Package References

**Files:**
- Modify: `src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj`

- [ ] **Step 1: Add required NuGet packages**

Add these package references to the test project csproj:

```xml
<PackageReference Include="YamlDotNet" Version="16.*" />
<PackageReference Include="Testcontainers" Version="4.*" />
<PackageReference Include="Testcontainers.Prometheus" Version="4.*" />
<PackageReference Include="Microsoft.Playwright" Version="1.*" />
```

- [ ] **Step 2: Restore packages**

Run: `dotnet restore src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj`
Expected: Restore succeeds with no errors.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj
git commit -m "chore: add Testcontainers, YamlDotNet, Playwright to test project"
```

---

## Task 2: Shared Contract — InstrumentDefinition Record

**Files:**
- Create: `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Contracts/InstrumentDefinition.cs`

- [ ] **Step 1: Create the InstrumentDefinition record**

```csharp
namespace Presentation.AgentHub.Tests.Telemetry.Contracts;

public enum InstrumentType
{
    Counter,
    UpDownCounter,
    Histogram,
    ObservableGauge
}

public sealed record InstrumentDefinition(
    string Name,
    InstrumentType Type,
    string? Unit = null)
{
    public string ToPrometheusName(string @namespace)
    {
        var baseName = Name.Replace('.', '_');
        var unitSuffix = GetUnitSuffix();
        var typeSuffix = GetTypeSuffix();

        return $"{@namespace}_{baseName}{unitSuffix}{typeSuffix}";
    }

    public IReadOnlyList<string> ToAllPrometheusNames(string @namespace)
    {
        var baseName = Name.Replace('.', '_');
        var unitSuffix = GetUnitSuffix();

        return Type switch
        {
            InstrumentType.Histogram => new[]
            {
                $"{@namespace}_{baseName}{unitSuffix}_sum",
                $"{@namespace}_{baseName}{unitSuffix}_count",
                $"{@namespace}_{baseName}{unitSuffix}_bucket"
            },
            InstrumentType.Counter => new[]
            {
                $"{@namespace}_{baseName}{unitSuffix}_total"
            },
            InstrumentType.UpDownCounter => new[]
            {
                $"{@namespace}_{baseName}{unitSuffix}"
            },
            InstrumentType.ObservableGauge => new[]
            {
                $"{@namespace}_{baseName}{unitSuffix}"
            },
            _ => new[] { $"{@namespace}_{baseName}{unitSuffix}" }
        };
    }

    private string GetUnitSuffix()
    {
        if (string.IsNullOrEmpty(Unit)) return string.Empty;

        // Curly-brace units are annotations only — no suffix
        if (Unit.StartsWith('{') && Unit.EndsWith('}')) return string.Empty;

        // Bare units get appended by the Prometheus exporter
        return Unit switch
        {
            "ms" => "_milliseconds",
            "s" => "_seconds",
            "ratio" => "_ratio",
            _ => $"_{Unit}"
        };
    }

    private string GetTypeSuffix()
    {
        return Type switch
        {
            InstrumentType.Counter => "_total",
            _ => string.Empty
        };
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Contracts/InstrumentDefinition.cs
git commit -m "feat: add InstrumentDefinition record with Prometheus naming rules"
```

---

## Task 3: Shared Contract — MetricNamingContract

**Files:**
- Create: `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Contracts/MetricNamingContract.cs`

- [ ] **Step 1: Create MetricNamingContract with all instruments**

```csharp
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Presentation.AgentHub.Tests.Telemetry.Contracts;

public static class MetricNamingContract
{
    public static readonly IReadOnlyList<InstrumentDefinition> AllInstruments = new[]
    {
        // SessionMetrics
        new InstrumentDefinition("agent.session.active", InstrumentType.UpDownCounter, "{session}"),
        new InstrumentDefinition("agent.session.cost", InstrumentType.Histogram, "{usd}"),
        new InstrumentDefinition("agent.session.started", InstrumentType.Counter, "{session}"),

        // OrchestrationMetrics
        new InstrumentDefinition("agent.orchestration.conversation_duration", InstrumentType.Histogram, "{ms}"),
        new InstrumentDefinition("agent.orchestration.turns_per_conversation", InstrumentType.Histogram, "{turn}"),
        new InstrumentDefinition("agent.orchestration.subagent_spawns", InstrumentType.Counter, "{spawn}"),
        new InstrumentDefinition("agent.orchestration.tool_call_count", InstrumentType.Counter, "{call}"),
        new InstrumentDefinition("agent.orchestration.turn_duration", InstrumentType.Histogram, "{ms}"),
        new InstrumentDefinition("agent.orchestration.turns_total", InstrumentType.Counter, "{turn}"),
        new InstrumentDefinition("agent.orchestration.turn_errors", InstrumentType.Counter),

        // TokenUsageMetrics
        new InstrumentDefinition("agent.tokens.input", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.tokens.output", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.tokens.total", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.tokens.budget_used", InstrumentType.UpDownCounter, "{token}"),

        // ContentSafetyMetrics
        new InstrumentDefinition("agent.safety.evaluations", InstrumentType.Counter, "{evaluation}"),
        new InstrumentDefinition("agent.safety.blocks", InstrumentType.Counter, "{block}"),
        new InstrumentDefinition("agent.safety.severity", InstrumentType.Histogram, "{level}"),
        new InstrumentDefinition("agent.safety.flags", InstrumentType.Counter, "{flag}"),
        new InstrumentDefinition("agent.safety.redactions", InstrumentType.Counter, "{redaction}"),

        // RagRetrievalMetrics
        new InstrumentDefinition("rag.retrieval.duration", InstrumentType.Histogram, "{ms}"),
        new InstrumentDefinition("rag.retrieval.chunks_returned", InstrumentType.Histogram, "{chunk}"),
        new InstrumentDefinition("rag.rerank.duration", InstrumentType.Histogram, "{ms}"),
        new InstrumentDefinition("rag.retrieval.queries", InstrumentType.Counter, "{query}"),
        new InstrumentDefinition("rag.retrieval.errors", InstrumentType.Counter),
        new InstrumentDefinition("rag.retrieval.hits", InstrumentType.Counter),
        new InstrumentDefinition("rag.source_retrievals", InstrumentType.Counter),
        new InstrumentDefinition("rag.grounding_score", InstrumentType.Histogram),

        // ToolExecutionMetrics
        new InstrumentDefinition("agent.tool.duration", InstrumentType.Histogram, "{ms}"),
        new InstrumentDefinition("agent.tool.invocations", InstrumentType.Counter, "{invocation}"),
        new InstrumentDefinition("agent.tool.errors", InstrumentType.Counter, "{error}"),
        new InstrumentDefinition("agent.tool.empty_results", InstrumentType.Counter, "{result}"),
        new InstrumentDefinition("agent.tool.result_size", InstrumentType.Histogram, "{char}"),

        // GovernanceMetrics
        new InstrumentDefinition("agent.governance.decisions", InstrumentType.Counter, "{decision}"),
        new InstrumentDefinition("agent.governance.violations", InstrumentType.Counter, "{violation}"),
        new InstrumentDefinition("agent.governance.evaluation_duration", InstrumentType.Histogram, "ms"),
        new InstrumentDefinition("agent.governance.rate_limit_hits", InstrumentType.Counter, "{hit}"),
        new InstrumentDefinition("agent.governance.audit_events", InstrumentType.Counter, "{event}"),
        new InstrumentDefinition("agent.governance.injection_detections", InstrumentType.Counter, "{detection}"),
        new InstrumentDefinition("agent.governance.mcp_scans", InstrumentType.Counter, "{scan}"),
        new InstrumentDefinition("agent.governance.mcp_threats", InstrumentType.Counter, "{threat}"),

        // ContextBudgetMetrics
        new InstrumentDefinition("agent.context.compactions", InstrumentType.Counter, "{compaction}"),
        new InstrumentDefinition("agent.context.system_prompt_tokens", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.context.skills_loaded_tokens", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.context.tools_schema_tokens", InstrumentType.Histogram, "{token}"),
        new InstrumentDefinition("agent.context.budget_utilization", InstrumentType.Histogram, "ratio"),
    };

    public static string GetCollectorNamespace(string collectorConfigPath)
    {
        var yaml = File.ReadAllText(collectorConfigPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var config = deserializer.Deserialize<Dictionary<string, object>>(yaml);

        // Navigate: exporters -> prometheus -> namespace
        var exporters = (Dictionary<object, object>)config["exporters"];
        var prometheus = (Dictionary<object, object>)exporters["prometheus"];
        return prometheus["namespace"].ToString()!;
    }

    public static IReadOnlyList<string> GetAllExpectedPrometheusNames(string collectorNamespace)
    {
        return AllInstruments
            .SelectMany(i => i.ToAllPrometheusNames(collectorNamespace))
            .OrderBy(n => n)
            .ToList();
    }

    public static IReadOnlySet<string> GetAllExpectedPrometheusNamesSet(string collectorNamespace)
    {
        return AllInstruments
            .SelectMany(i => i.ToAllPrometheusNames(collectorNamespace))
            .ToHashSet();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Contracts/MetricNamingContract.cs
git commit -m "feat: add MetricNamingContract with all instrument definitions and Prometheus transform"
```

---

## Task 4: Layer 1 — MetricsIntegrationFactory

**Files:**
- Create: `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Fixtures/MetricsIntegrationFactory.cs`

- [ ] **Step 1: Create the factory that wires a mock AI client and Prometheus exporter**

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using OpenTelemetry.Metrics;
using AgenticHarness.Application.AI.Common.Interfaces;
using AgenticHarness.Domain.Common.Telemetry;

namespace Presentation.AgentHub.Tests.Telemetry.Fixtures;

public class MetricsIntegrationFactory : TestWebApplicationFactory
{
    private readonly Mock<IChatClientFactory> _mockChatClientFactory = new();

    public MetricsIntegrationFactory()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Test response from mock AI.")));

        _mockChatClientFactory
            .Setup(f => f.GetChatClientAsync(
                It.IsAny<AIAgentFrameworkClientType>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockChatClient.Object);

        _mockChatClientFactory
            .Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>()))
            .Returns(true);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // Replace IChatClientFactory with mock
            services.RemoveAll<IChatClientFactory>();
            services.AddSingleton(_mockChatClientFactory.Object);

            // Ensure Prometheus exporter is wired for scraping
            services.RemoveAll<MeterProvider>();
            services.AddOpenTelemetry()
                .WithMetrics(m =>
                {
                    m.AddMeter(AppSourceNames.AgenticHarness);
                    m.AddPrometheusExporter();
                });
        });
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Fixtures/MetricsIntegrationFactory.cs
git commit -m "feat: add MetricsIntegrationFactory with mock AI client and Prometheus exporter"
```

---

## Task 5: Layer 1 — MetricsIntegrationTests

**Files:**
- Create: `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/MetricsIntegrationTests.cs`

- [ ] **Step 1: Write the failing integration tests**

```csharp
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Presentation.AgentHub.Tests.Telemetry.Contracts;
using Presentation.AgentHub.Tests.Telemetry.Fixtures;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry;

[Trait("Category", "Integration")]
public class MetricsIntegrationTests : IClassFixture<MetricsIntegrationFactory>, IAsyncLifetime
{
    private readonly MetricsIntegrationFactory _factory;
    private readonly HttpClient _client;

    public MetricsIntegrationTests(MetricsIntegrationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "integration-test-user");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RealConversation_EmitsSessionMetrics()
    {
        // Arrange — start a conversation via the AG-UI HTTP endpoint
        var conversationId = Guid.NewGuid().ToString();
        var request = new
        {
            conversationId,
            message = "Hello, this is a test message"
        };

        // Act — send a chat turn
        var response = await _client.PostAsJsonAsync("/api/agui/run", request);
        response.EnsureSuccessStatusCode();

        // Force flush metrics
        var provider = _factory.Services.GetService<MeterProvider>();
        provider?.ForceFlush();

        // Scrape prometheus endpoint
        var metrics = await _client.GetStringAsync("/metrics");

        // Assert — session metrics were emitted
        metrics.Should().Contain("agent_session_started");
        metrics.Should().Contain("agent_session_active");
    }

    [Fact]
    public async Task RealConversation_EmitsOrchestrationMetrics()
    {
        var conversationId = Guid.NewGuid().ToString();
        var request = new
        {
            conversationId,
            message = "Test orchestration metrics"
        };

        var response = await _client.PostAsJsonAsync("/api/agui/run", request);
        response.EnsureSuccessStatusCode();

        var provider = _factory.Services.GetService<MeterProvider>();
        provider?.ForceFlush();

        var metrics = await _client.GetStringAsync("/metrics");

        metrics.Should().Contain("agent_orchestration_turns_total");
        metrics.Should().Contain("agent_orchestration_turn_duration");
    }

    [Fact]
    public async Task RealConversation_EmitsTokenMetrics()
    {
        var conversationId = Guid.NewGuid().ToString();
        var request = new
        {
            conversationId,
            message = "Test token metrics"
        };

        var response = await _client.PostAsJsonAsync("/api/agui/run", request);
        response.EnsureSuccessStatusCode();

        var provider = _factory.Services.GetService<MeterProvider>();
        provider?.ForceFlush();

        var metrics = await _client.GetStringAsync("/metrics");

        metrics.Should().Contain("agent_tokens_input");
        metrics.Should().Contain("agent_tokens_output");
    }

    [Fact]
    public async Task RealConversation_NoMetricIsDoublePrefixed()
    {
        var conversationId = Guid.NewGuid().ToString();
        var request = new
        {
            conversationId,
            message = "Test double prefix guard"
        };

        var response = await _client.PostAsJsonAsync("/api/agui/run", request);
        response.EnsureSuccessStatusCode();

        var provider = _factory.Services.GetService<MeterProvider>();
        provider?.ForceFlush();

        var metrics = await _client.GetStringAsync("/metrics");

        // App-level Prometheus exporter should NOT have any namespace prefix
        // (the collector adds it). If we see agentic_harness_ here, something is wrong.
        metrics.Should().NotContain("agentic_harness_agentic_harness_");
        metrics.Should().NotContain("agentic_harness_agent_");
    }

    [Fact]
    public async Task MultipleTurns_IncrementsTurnCounter()
    {
        var conversationId = Guid.NewGuid().ToString();

        for (var i = 0; i < 3; i++)
        {
            var request = new { conversationId, message = $"Turn {i + 1}" };
            var response = await _client.PostAsJsonAsync("/api/agui/run", request);
            response.EnsureSuccessStatusCode();
        }

        var provider = _factory.Services.GetService<MeterProvider>();
        provider?.ForceFlush();

        var metrics = await _client.GetStringAsync("/metrics");

        // turns_total counter should show value >= 3
        metrics.Should().Contain("agent_orchestration_turns_total");
        var lines = metrics.Split('\n');
        var turnLine = lines.FirstOrDefault(l =>
            l.StartsWith("agent_orchestration_turns_total") && !l.StartsWith("#"));
        turnLine.Should().NotBeNull();

        // Parse the counter value
        var value = double.Parse(turnLine!.Split(' ').Last());
        value.Should().BeGreaterThanOrEqualTo(3);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "Category=Integration" -v n`
Expected: Tests FAIL because the factory/endpoint wiring needs adjustment. This confirms the tests exercise real code paths.

- [ ] **Step 3: Fix any wiring issues until tests pass**

Adjust the factory, endpoint path, or request shape based on actual compilation errors and runtime failures. The AG-UI endpoint may require a different request body shape or authentication header. Check `AgUiRunHandler.cs` for the exact route and model.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "Category=Integration" -v n`
Expected: All 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/MetricsIntegrationTests.cs
git commit -m "feat: add Layer 1 integration tests — real chat flow to metrics emission"
```

---

## Task 6: Layer 2 — CollectorContractTests

**Files:**
- Create: `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/CollectorContractTests.cs`

- [ ] **Step 1: Write the collector contract tests**

```csharp
using FluentAssertions;
using Presentation.AgentHub.Tests.Telemetry.Contracts;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry;

[Trait("Category", "Contract")]
public class CollectorContractTests
{
    private static readonly string CollectorConfigPath = Path.Combine(
        GetRepoRoot(), "scripts", "otel-collector", "config.yaml");

    [Fact]
    public void CollectorConfig_HasExpectedNamespace()
    {
        var ns = MetricNamingContract.GetCollectorNamespace(CollectorConfigPath);
        ns.Should().Be("agentic_harness",
            "dashboard PromQL queries assume this prefix");
    }

    [Fact]
    public void AllInstruments_ProduceValidPrometheusNames()
    {
        const string ns = "agentic_harness";
        var names = MetricNamingContract.GetAllExpectedPrometheusNames(ns);

        names.Should().NotBeEmpty();
        names.Should().OnlyContain(n => !n.Contains('.'),
            "Prometheus metric names cannot contain dots");
        names.Should().OnlyContain(n => !n.Contains("__"),
            "Double underscores indicate a naming bug");
    }

    [Fact]
    public void CounterInstruments_GetTotalSuffix()
    {
        const string ns = "agentic_harness";
        var counters = MetricNamingContract.AllInstruments
            .Where(i => i.Type == InstrumentType.Counter);

        foreach (var counter in counters)
        {
            var names = counter.ToAllPrometheusNames(ns);
            names.Should().OnlyContain(n => n.EndsWith("_total"),
                $"Counter '{counter.Name}' should produce _total suffix");
        }
    }

    [Fact]
    public void HistogramInstruments_GetSumCountBucket()
    {
        const string ns = "agentic_harness";
        var histograms = MetricNamingContract.AllInstruments
            .Where(i => i.Type == InstrumentType.Histogram);

        foreach (var hist in histograms)
        {
            var names = hist.ToAllPrometheusNames(ns);
            names.Should().Contain(n => n.EndsWith("_sum"));
            names.Should().Contain(n => n.EndsWith("_count"));
            names.Should().Contain(n => n.EndsWith("_bucket"));
        }
    }

    [Fact]
    public void BareUnitMs_ProducesMillisecondsSuffix()
    {
        const string ns = "agentic_harness";
        // GovernanceMetrics.EvaluationDuration uses bare "ms" unit
        var governance = MetricNamingContract.AllInstruments
            .First(i => i.Name == "agent.governance.evaluation_duration");

        var names = governance.ToAllPrometheusNames(ns);
        names.Should().Contain(n => n.Contains("_milliseconds_"),
            "Bare 'ms' unit should produce _milliseconds suffix");
    }

    [Fact]
    public void CurlyBraceUnits_ProduceNoUnitSuffix()
    {
        const string ns = "agentic_harness";
        // agent.session.started has unit "{session}" — annotation only
        var started = MetricNamingContract.AllInstruments
            .First(i => i.Name == "agent.session.started");

        var names = started.ToAllPrometheusNames(ns);
        names.Should().NotContain(n => n.Contains("_session"),
            "Curly-brace units are annotations and should not appear in the name");
        names.Should().Contain("agentic_harness_agent_session_started_total");
    }

    [Fact]
    public void Snapshot_AllPrometheusNames_MatchesExpected()
    {
        const string ns = "agentic_harness";
        var names = MetricNamingContract.GetAllExpectedPrometheusNames(ns);

        // Snapshot: if this test fails, either an instrument was added/renamed/removed
        // or the naming rules changed. Update AllInstruments in MetricNamingContract.
        names.Count.Should().BeGreaterThan(80,
            "Expected 80+ Prometheus metric name variants from all instruments");

        // Spot-check critical metrics the dashboard depends on
        var nameSet = names.ToHashSet();
        nameSet.Should().Contain("agentic_harness_agent_session_active");
        nameSet.Should().Contain("agentic_harness_agent_session_started_total");
        nameSet.Should().Contain("agentic_harness_agent_orchestration_turns_total");
        nameSet.Should().Contain("agentic_harness_agent_orchestration_turn_duration_sum");
        nameSet.Should().Contain("agentic_harness_agent_tool_invocations_total");
        nameSet.Should().Contain("agentic_harness_agent_governance_evaluation_duration_milliseconds_sum");
    }

    private static string GetRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "Category=Contract" -v n`
Expected: All 7 tests PASS. If any fail, the naming rules in `InstrumentDefinition` need adjustment.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/CollectorContractTests.cs
git commit -m "feat: add Layer 2 collector contract tests — naming transform validation"
```

---

## Task 7: Layer 3 — DashboardContractTests

**Files:**
- Create: `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/DashboardContractTests.cs`

- [ ] **Step 1: Write the dashboard contract tests**

```csharp
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Presentation.AgentHub.Tests.Telemetry.Contracts;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry;

[Trait("Category", "Contract")]
public partial class DashboardContractTests
{
    private const string Namespace = "agentic_harness";

    [GeneratedRegex(@"(agentic_harness_[a-z][a-z0-9_]+)", RegexOptions.Compiled)]
    private static partial Regex MetricNamePattern();

    [Fact]
    public void AllDashboardMetrics_ExistInContract()
    {
        var dashboardMetricNames = ExtractMetricNamesFromCatalog();
        var contractNames = MetricNamingContract.GetAllExpectedPrometheusNamesSet(Namespace);

        var missing = new List<string>();
        foreach (var name in dashboardMetricNames)
        {
            // Strip suffixes that Prometheus auto-adds for TYPE matching
            var baseName = StripPrometheusSuffix(name);
            var found = contractNames.Contains(name)
                     || contractNames.Contains(baseName)
                     || contractNames.Any(c => c.StartsWith(baseName));

            if (!found)
                missing.Add(name);
        }

        missing.Should().BeEmpty(
            $"Dashboard references metrics not in the contract: [{string.Join(", ", missing)}]. " +
            "Either add the instrument to MetricNamingContract.AllInstruments or fix the PromQL query.");
    }

    [Fact]
    public void AllContractMetrics_HaveDashboardCoverage()
    {
        var dashboardMetricNames = ExtractMetricNamesFromCatalog();
        var contractBaseNames = MetricNamingContract.AllInstruments
            .Select(i => $"{Namespace}_{i.Name.Replace('.', '_')}")
            .ToHashSet();

        var uncovered = new List<string>();
        foreach (var baseName in contractBaseNames)
        {
            if (!dashboardMetricNames.Any(d => d.StartsWith(baseName)))
                uncovered.Add(baseName);
        }

        // This is a soft assertion — not all metrics need dashboard panels,
        // but coverage gaps should be intentional
        if (uncovered.Count > 0)
        {
            // Log warning but don't fail — some metrics are for alerting only
            uncovered.Count.Should().BeLessThan(contractBaseNames.Count / 2,
                $"More than half of contract metrics have no dashboard coverage: [{string.Join(", ", uncovered.Take(10))}...]");
        }
    }

    [Fact]
    public void DashboardQueries_UseCorrectNamespacePrefix()
    {
        var queries = GetAllPromQLQueries();

        foreach (var query in queries)
        {
            // Any metric name in the query should start with the namespace
            var matches = MetricNamePattern().Matches(query);
            foreach (Match match in matches)
            {
                match.Value.Should().StartWith($"{Namespace}_",
                    $"Query '{query}' contains metric without correct namespace prefix");
            }
        }
    }

    private static IReadOnlySet<string> ExtractMetricNamesFromCatalog()
    {
        var queries = GetAllPromQLQueries();
        var names = new HashSet<string>();

        foreach (var query in queries)
        {
            var matches = MetricNamePattern().Matches(query);
            foreach (Match match in matches)
                names.Add(match.Value);
        }

        return names;
    }

    private static IReadOnlyList<string> GetAllPromQLQueries()
    {
        // Load MetricCatalog entries via reflection to get PromQL strings
        var controllerAssembly = typeof(Program).Assembly;
        var catalogType = controllerAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "MetricCatalog")
            ?? throw new InvalidOperationException(
                "MetricCatalog type not found in Presentation.AgentHub assembly");

        var entriesField = catalogType.GetField("Entries",
            BindingFlags.Public | BindingFlags.Static)
            ?? catalogType.GetProperty("Entries",
                BindingFlags.Public | BindingFlags.Static);

        var entries = entriesField switch
        {
            FieldInfo f => f.GetValue(null),
            PropertyInfo p => p.GetValue(null),
            _ => throw new InvalidOperationException("Could not access MetricCatalog.Entries")
        };

        var queryProp = entries!.GetType().GetGenericArguments()[0]
            .GetProperty("Query") ?? throw new InvalidOperationException("No Query property");

        return ((System.Collections.IEnumerable)entries)
            .Cast<object>()
            .Select(e => queryProp.GetValue(e)?.ToString() ?? "")
            .Where(q => !string.IsNullOrEmpty(q))
            .ToList();
    }

    private static string StripPrometheusSuffix(string name)
    {
        string[] suffixes = ["_total", "_sum", "_count", "_bucket",
            "_milliseconds_sum", "_milliseconds_count", "_milliseconds_bucket",
            "_ratio_sum", "_ratio_count", "_ratio_bucket"];

        foreach (var suffix in suffixes.OrderByDescending(s => s.Length))
        {
            if (name.EndsWith(suffix))
                return name[..^suffix.Length];
        }
        return name;
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "Category=Contract" -v n`
Expected: All contract tests pass. If `AllDashboardMetrics_ExistInContract` fails, it reveals the specific metrics the dashboard references that aren't in the contract — these are likely the root cause of the empty dashboard.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/DashboardContractTests.cs
git commit -m "feat: add Layer 3 dashboard contract tests — PromQL validation against naming contract"
```

---

## Task 8: Layer 4 — PrometheusFixture (Testcontainers)

**Files:**
- Create: `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Fixtures/PrometheusFixture.cs`

- [ ] **Step 1: Create the Testcontainers fixture**

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry.Fixtures;

public sealed class PrometheusFixture : IAsyncLifetime
{
    private INetwork _network = null!;
    private IContainer _collector = null!;
    private IContainer _prometheus = null!;

    public string CollectorOtlpGrpcEndpoint { get; private set; } = null!;
    public string CollectorOtlpHttpEndpoint { get; private set; } = null!;
    public string PrometheusQueryEndpoint { get; private set; } = null!;

    private static readonly string RepoRoot = GetRepoRoot();

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder()
            .WithName($"telemetry-e2e-{Guid.NewGuid():N}")
            .Build();

        await _network.CreateAsync();

        // Start OTel Collector
        _collector = new ContainerBuilder()
            .WithImage("otel/opentelemetry-collector-contrib:0.123.0")
            .WithName($"collector-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("otel-collector")
            .WithPortBinding(4317, true)  // OTLP gRPC
            .WithPortBinding(4318, true)  // OTLP HTTP
            .WithPortBinding(8889, true)  // Prometheus exporter
            .WithResourceMapping(
                Path.Combine(RepoRoot, "scripts", "otel-collector", "config.yaml"),
                "/etc/otelcol-contrib/config.yaml")
            .WithEnvironment("DEPLOYMENT_ENVIRONMENT", "test")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(4317)
                .UntilPortIsAvailable(8889))
            .Build();

        await _collector.StartAsync();

        // Write Prometheus config targeting the collector
        var promConfig = $"""
            global:
              scrape_interval: 2s
              evaluation_interval: 2s

            scrape_configs:
              - job_name: 'otel-collector'
                static_configs:
                  - targets: ['otel-collector:8889']
            """;

        var promConfigPath = Path.Combine(Path.GetTempPath(), $"prom-{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(promConfigPath, promConfig);

        // Start Prometheus
        _prometheus = new ContainerBuilder()
            .WithImage("prom/prometheus:v2.53.0")
            .WithName($"prometheus-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithPortBinding(9090, true)
            .WithResourceMapping(promConfigPath, "/etc/prometheus/prometheus.yml")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(9090))
            .Build();

        await _prometheus.StartAsync();

        CollectorOtlpGrpcEndpoint = $"http://localhost:{_collector.GetMappedPublicPort(4317)}";
        CollectorOtlpHttpEndpoint = $"http://localhost:{_collector.GetMappedPublicPort(4318)}";
        PrometheusQueryEndpoint = $"http://localhost:{_prometheus.GetMappedPublicPort(9090)}";
    }

    public async Task<JsonElement?> QueryPrometheus(string promql)
    {
        using var client = new HttpClient();
        var url = $"{PrometheusQueryEndpoint}/api/v1/query?query={Uri.EscapeDataString(promql)}";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var result = json.GetProperty("data").GetProperty("result");
        return result.GetArrayLength() > 0 ? result : null;
    }

    public async Task<bool> WaitForMetric(string promql, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = await QueryPrometheus(promql);
            if (result.HasValue)
                return true;
            await Task.Delay(2000);
        }
        return false;
    }

    public async Task DisposeAsync()
    {
        if (_prometheus != null) await _prometheus.DisposeAsync();
        if (_collector != null) await _collector.DisposeAsync();
        if (_network != null) await _network.DeleteAsync();
    }

    private static string GetRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/Fixtures/PrometheusFixture.cs
git commit -m "feat: add PrometheusFixture with Testcontainers for OTel Collector + Prometheus"
```

---

## Task 9: Layer 4 — MetricsE2ETests

**Files:**
- Create: `src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/MetricsE2ETests.cs`

- [ ] **Step 1: Write the E2E test**

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.Tests.Telemetry.Contracts;
using Presentation.AgentHub.Tests.Telemetry.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Presentation.AgentHub.Tests.Telemetry;

[Trait("Category", "E2E")]
[Collection("E2E")]
public class MetricsE2ETests : IClassFixture<PrometheusFixture>, IAsyncLifetime
{
    private readonly PrometheusFixture _prometheus;
    private readonly ITestOutputHelper _output;
    private MetricsIntegrationFactory _factory = null!;
    private HttpClient _client = null!;

    public MetricsE2ETests(PrometheusFixture prometheus, ITestOutputHelper output)
    {
        _prometheus = prometheus;
        _output = output;
    }

    public Task InitializeAsync()
    {
        // Configure app to send OTLP to the testcontainer collector
        _factory = new MetricsIntegrationFactory();
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", _prometheus.CollectorOtlpHttpEndpoint);
            builder.UseSetting("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "e2e-test-user");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task FullPipeline_ChatProducesPrometheusData()
    {
        // Act — send a real chat through the app
        var request = new
        {
            conversationId = Guid.NewGuid().ToString(),
            message = "E2E telemetry pipeline test"
        };
        var response = await _client.PostAsJsonAsync("/api/agui/run", request);
        response.EnsureSuccessStatusCode();

        _output.WriteLine("Chat sent successfully. Waiting for metrics to propagate...");

        // Wait for metrics to flow: App → Collector → Prometheus
        var found = await _prometheus.WaitForMetric(
            "agentic_harness_agent_session_started_total", TimeSpan.FromSeconds(30));

        found.Should().BeTrue(
            "Metrics should propagate from app through collector to Prometheus within 30s. " +
            "If this fails, check: (1) service.name matches collector filter, " +
            "(2) OTLP endpoint is reachable, (3) Prometheus is scraping collector port 8889");
    }

    [Fact]
    public async Task FullPipeline_OrchestrationMetricsReachPrometheus()
    {
        var request = new
        {
            conversationId = Guid.NewGuid().ToString(),
            message = "E2E orchestration test"
        };
        var response = await _client.PostAsJsonAsync("/api/agui/run", request);
        response.EnsureSuccessStatusCode();

        var found = await _prometheus.WaitForMetric(
            "agentic_harness_agent_orchestration_turns_total", TimeSpan.FromSeconds(30));

        found.Should().BeTrue("Orchestration turn counter should reach Prometheus");
    }

    [Fact]
    public async Task FullPipeline_NoDoublePrefixInPrometheus()
    {
        var request = new
        {
            conversationId = Guid.NewGuid().ToString(),
            message = "E2E double-prefix guard"
        };
        var response = await _client.PostAsJsonAsync("/api/agui/run", request);
        response.EnsureSuccessStatusCode();

        await Task.Delay(5000); // Wait for propagation

        // Query for the bad pattern
        var doublePrefix = await _prometheus.QueryPrometheus(
            "{__name__=~\"agentic_harness_agentic_harness_.*\"}");

        doublePrefix.Should().BeNull(
            "No metrics should have double-prefixed names in Prometheus");
    }

    [Fact]
    public async Task FullPipeline_CollectorFilterAcceptsAppTraffic()
    {
        // If the service.name doesn't match "Presentation.AgentHub",
        // the collector's filter/app_only drops everything silently.
        var request = new
        {
            conversationId = Guid.NewGuid().ToString(),
            message = "Service name filter test"
        };
        var response = await _client.PostAsJsonAsync("/api/agui/run", request);
        response.EnsureSuccessStatusCode();

        // Check that ANY metric from our app reached Prometheus
        var found = await _prometheus.WaitForMetric(
            "{__name__=~\"agentic_harness_agent_.*\"}", TimeSpan.FromSeconds(30));

        found.Should().BeTrue(
            "App metrics should pass through the collector's filter/app_only processor. " +
            "If this fails, the service.name resource attribute doesn't match " +
            "the regex 'Presentation\\.AgentHub' in the collector config.");
    }
}
```

- [ ] **Step 2: Run E2E tests (requires Docker)**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "Category=E2E" -v n`
Expected: All 4 tests PASS (or fail with a clear diagnostic message pointing to the broken pipeline segment).

- [ ] **Step 3: Commit**

```bash
git add src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/MetricsE2ETests.cs
git commit -m "feat: add Layer 4 E2E tests — full pipeline with Testcontainers (app → collector → Prometheus)"
```

---

## Task 10: Root Cause Fix (if needed)

After running all tests, at least one layer will likely fail and reveal the root cause. Common fixes:

- [ ] **Step 1: If Layer 1 fails** — The handler isn't emitting metrics during real execution. Check that the mock `IChatClient` returns enough data for the handler to proceed past the AI call to the metrics emission code.

- [ ] **Step 2: If Layer 2 fails** — An instrument name in the code doesn't match what `MetricNamingContract` declares. Update the contract OR the instrument definition to match.

- [ ] **Step 3: If Layer 3 fails** — The dashboard's PromQL references a metric that doesn't exist after the transform. Fix the PromQL in `MetricCatalog` to use the correct name from the contract.

- [ ] **Step 4: If Layer 4 fails** — The full pipeline is broken. The failure message indicates which segment: service.name filter, OTLP endpoint, or Prometheus scrape. Fix the configuration.

- [ ] **Step 5: Re-run all tests**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "Category=Integration|Category=Contract|Category=E2E" -v n`
Expected: ALL tests pass.

- [ ] **Step 6: Commit the fix**

```bash
git add -A
git commit -m "fix: [describe the root cause fix based on which layer failed]"
```

---

## Task 11: Verify Full Suite

- [ ] **Step 1: Run entire test suite**

Run: `dotnet test src/AgenticHarness.slnx -v n`
Expected: No regressions. All existing + new tests pass.

- [ ] **Step 2: Final commit if any cleanup needed**

```bash
git add -A
git commit -m "test: telemetry verification suite complete — all 4 layers passing"
```
