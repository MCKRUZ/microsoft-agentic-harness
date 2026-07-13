using System.Reflection;
using System.Text.RegularExpressions;
using Application.Core.CQRS.Agents.RunConversation;
using FluentAssertions;
using FluentAssertions.Execution;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Presentation.AgentHub.Tests.Telemetry.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Presentation.AgentHub.Tests.Telemetry;

/// <summary>
/// Reconciles each dashboard tile against what a real agent turn actually EMITS — the gap
/// that <see cref="DashboardContractTests"/> (names vs a declared contract) and the SPA
/// component tests (rendered against mock data) both miss.
///
/// A tile is "broken" when its PromQL references a metric the running system never records.
/// Because most catalog queries end in <c>or vector(0)</c>, such a tile renders <c>0</c> rather
/// than blank — it looks healthy while showing nothing. This turns "which tiles actually have
/// data?" into a test you run, instead of clicking every panel.
///
/// The <see cref="Tiles"/> table is the source of truth, classifying every catalog tile (by its
/// catalog <c>Id</c>) as:
/// <list type="bullet">
///   <item><see cref="TileStatus.AlwaysOn"/> — its metric emits on any basic turn. Enforced by
///   <see cref="AlwaysOnTiles_EmitOnABasicTurn"/>: a regression here means the working half of the
///   dashboard went dark (e.g. the app stopped exporting to the collector).</item>
///   <item><see cref="TileStatus.Conditional"/> — emits only when a specific thing happens (a tool
///   call, a cache hit, a safety block, a configured budget, real LLM token usage). Legitimately
///   blank on a no-op turn.</item>
///   <item><see cref="TileStatus.KnownGap"/> — SHOULD have data but never will, because of a
///   confirmed bug. Each carries the reason. Fixing the bug means moving the tile out of KnownGap,
///   at which point the emission guard starts enforcing it.</item>
/// </list>
/// </summary>
[Trait("Category", "Telemetry")]
[Trait("Category", "Integration")]
public sealed partial class DashboardMetricEmissionTests : IClassFixture<MetricsIntegrationFactory>
{
    private readonly MetricsIntegrationFactory _factory;
    private readonly ITestOutputHelper _output;

    public DashboardMetricEmissionTests(MetricsIntegrationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    /// <summary>Whether a dashboard tile has real data behind it.</summary>
    public enum TileStatus
    {
        /// <summary>Metric emits on any basic turn; the emission guard enforces it.</summary>
        AlwaysOn,

        /// <summary>Emits only when a specific activity occurs; blank on a no-op turn is fine.</summary>
        Conditional,

        /// <summary>Should have data but never will, due to a confirmed bug.</summary>
        KnownGap,
    }

    /// <summary>
    /// Every dashboard tile, keyed by its catalog <c>Id</c>, with its data status and a reason.
    /// Asserted complete by <see cref="EveryDashboardTile_IsClassified"/> — a new tile cannot be
    /// added without recording whether it actually has data.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (TileStatus Status, string Note)> Tiles =
        new Dictionary<string, (TileStatus, string)>
        {
            // ── Overview ──
            ["tokens_per_minute"] = (TileStatus.Conditional, "needs real LLM token usage"),
            ["active_sessions"] = (TileStatus.AlwaysOn, "agent_session_active toggles every session"),
            ["cost_today"] = (TileStatus.Conditional, "needs real LLM token usage; cost now priced via the default-model fallback (fixed 2026-06-22)"),
            ["cache_hit_rate"] = (TileStatus.Conditional, "needs real LLM token usage"),
            ["safety_violations"] = (TileStatus.AlwaysOn, "ContentSafetyBehavior emits evaluations every turn"),
            ["budget_status"] = (TileStatus.Conditional, "budget gauges only register when BudgetTracking.Enabled=true (dormant by default)"),

            // ── Tokens ──
            ["tokens_input_total"] = (TileStatus.Conditional, "needs real LLM token usage (works in prod, not with the stub)"),
            ["tokens_output_total"] = (TileStatus.Conditional, "needs real LLM token usage"),
            ["tokens_cache_read"] = (TileStatus.Conditional, "needs a prompt-cache read"),
            ["tokens_cache_write"] = (TileStatus.Conditional, "needs a prompt-cache write"),
            ["tokens_input_rate"] = (TileStatus.Conditional, "needs real LLM token usage"),
            ["tokens_output_rate"] = (TileStatus.Conditional, "needs real LLM token usage"),
            ["tokens_by_model"] = (TileStatus.Conditional, "needs real LLM token usage"),
            ["tokens_cache_hit_rate_ts"] = (TileStatus.Conditional, "needs real LLM token usage"),

            // ── Cost (fixed 2026-06-22: cost now prices via the default-model fallback in
            //    LlmUsageCapture + LlmTokenTrackingProcessor; needs real token usage to be non-zero) ──
            ["cost_total"] = (TileStatus.Conditional, "needs real LLM token usage (fixed: default-model pricing fallback)"),
            ["cost_rate"] = (TileStatus.Conditional, "needs real LLM token usage (fixed)"),
            ["cost_by_model"] = (TileStatus.Conditional, "needs real LLM token usage (fixed)"),
            ["cost_cache_savings"] = (TileStatus.Conditional, "needs cache-read tokens (fixed: default-model pricing fallback)"),
            ["cost_budget_remaining"] = (TileStatus.Conditional, "budget thresholds only register when BudgetTracking.Enabled=true"),

            // ── Tools (counts work once a tool runs; quality metrics are a gap) ──
            ["tools_calls_total"] = (TileStatus.Conditional, "needs a tool call (agent_tool_invocations works in prod)"),
            ["tools_errors_total"] = (TileStatus.Conditional, "needs a tool call that errors; the execute_tool span now emits after OpenTelemetry was composed below UseFunctionInvocation (fixed 2026-07-12)"),
            ["tools_avg_latency"] = (TileStatus.Conditional, "needs a tool call; the execute_tool span now emits (fixed 2026-07-12: OTel below function invocation)"),
            ["tools_result_size"] = (TileStatus.Conditional, "needs a tool call; the execute_tool span now emits (fixed 2026-07-12: OTel below function invocation)"),
            ["tools_calls_by_tool"] = (TileStatus.Conditional, "needs a tool call"),
            ["tools_latency_by_tool"] = (TileStatus.Conditional, "needs a tool call; the execute_tool span now emits (fixed 2026-07-12: OTel below function invocation)"),
            ["tools_error_rate"] = (TileStatus.Conditional, "needs a tool call that errors; the execute_tool span now emits (fixed 2026-07-12: OTel below function invocation)"),

            // ── Safety ──
            ["safety_total"] = (TileStatus.AlwaysOn, "evaluations emit every turn"),
            ["safety_blocked"] = (TileStatus.Conditional, "needs content the safety filter blocks"),
            ["safety_violations_ts"] = (TileStatus.AlwaysOn, "evaluations emit every turn"),
            ["safety_by_category"] = (TileStatus.AlwaysOn, "evaluations emit every turn (category tag present)"),
            ["safety_block_rate"] = (TileStatus.Conditional, "needs a block to be non-zero"),

            // ── Sessions ──
            ["sessions_total"] = (TileStatus.AlwaysOn, "session_started emits every conversation"),
            ["sessions_active"] = (TileStatus.AlwaysOn, "session_active toggles every session"),
            ["sessions_turns_avg"] = (TileStatus.AlwaysOn, "turns_per_conversation emits at conversation end"),
            ["sessions_duration_avg"] = (TileStatus.AlwaysOn, "conversation_duration emits at conversation end"),
            ["sessions_active_ts"] = (TileStatus.AlwaysOn, "session_active toggles every session"),
            ["sessions_turns_ts"] = (TileStatus.AlwaysOn, "turns_total emits every turn (fixed 2026-06-22: removed the doubled _total suffix in the catalog query + the naming contract)"),

            // ── RAG (only runs when the agent exercises retrieval/ingestion tools) ──
            ["rag_ingestion_total"] = (TileStatus.Conditional, "needs a document ingestion (fixed 2026-06-22: Documents.Add now called in IngestDocumentCommandHandler)"),
            ["rag_retrieval_total"] = (TileStatus.Conditional, "needs a RAG retrieval; also skipped on the routed-pipeline path"),
            ["rag_avg_latency"] = (TileStatus.Conditional, "needs a RAG retrieval"),
            ["rag_chunks_avg"] = (TileStatus.Conditional, "needs a RAG retrieval"),
            ["rag_ingestion_rate"] = (TileStatus.Conditional, "needs a document ingestion (fixed: rag_ingestion_documents now incremented)"),
            ["rag_retrieval_latency_ts"] = (TileStatus.Conditional, "needs a RAG retrieval"),

            // ── Budget (dormant until a consumer enables + configures budgets) ──
            ["budget_spent"] = (TileStatus.Conditional, "ObservableGauge dormant unless BudgetTracking.Enabled=true"),
            ["budget_limit"] = (TileStatus.Conditional, "ObservableGauge dormant unless BudgetTracking.Enabled=true"),
            ["budget_remaining"] = (TileStatus.Conditional, "ObservableGauge dormant unless BudgetTracking.Enabled=true"),
            ["budget_utilization"] = (TileStatus.Conditional, "ObservableGauge dormant unless BudgetTracking.Enabled=true"),
            ["budget_spend_rate"] = (TileStatus.Conditional, "session_cost needs real LLM token usage"),
        };

    [GeneratedRegex(@"agentic_harness_[a-z][a-z0-9_]+")]
    private static partial Regex MetricNamePattern();

    /// <summary>
    /// Every tile in the catalog must appear in <see cref="Tiles"/>. Fails when a new dashboard
    /// tile is added without recording whether it actually has data — the omission that let the
    /// cost/tool/rag tiles ship silently broken.
    /// </summary>
    [Fact]
    public void EveryDashboardTile_IsClassified()
    {
        var catalogIds = GetCatalogEntries().Select(e => e.Id).Distinct().ToList();
        catalogIds.Should().NotBeEmpty("the catalog must define at least one tile");

        using var _ = new AssertionScope();
        foreach (var id in catalogIds.OrderBy(i => i))
            Tiles.ContainsKey(id).Should().BeTrue(
                $"dashboard tile '{id}' is not classified in {nameof(DashboardMetricEmissionTests)}." +
                $"{nameof(Tiles)} — add it as AlwaysOn, Conditional, or KnownGap so its data status is tracked");
    }

    /// <summary>
    /// Runs a real agent turn and asserts every <see cref="TileStatus.AlwaysOn"/> tile's metric
    /// was actually emitted. This is the alarm that fires when the working half of the dashboard
    /// goes dark — the failure a mock-based render test cannot see.
    /// </summary>
    [Fact]
    public async Task AlwaysOnTiles_EmitOnABasicTurn()
    {
        await RunBasicTurnAsync();
        var scrape = await ScrapeMetricsAsync();

        // ToLookup (not ToDictionary) tolerates the catalog's duplicate tile Ids — e.g.
        // "budget_status" is defined in both the Overview and Budget categories.
        var byId = GetCatalogEntries().ToLookup(e => e.Id, e => e.Query);

        var failures = new List<string>();
        foreach (var (id, _) in Tiles.Where(t => t.Value.Status == TileStatus.AlwaysOn))
        {
            foreach (var query in byId[id]) // empty when the Id is absent; coverage test owns Id drift
            foreach (var prefixed in MetricNamePattern().Matches(query).Select(m => m.Value).Distinct())
            {
                var unprefixed = prefixed["agentic_harness_".Length..];
                if (!scrape.Contains(unprefixed, StringComparison.Ordinal))
                    failures.Add($"tile '{id}' expects '{unprefixed}' but it was not emitted");
            }
        }

        if (failures.Count > 0)
            _output.WriteLine("Emitted agent/rag metrics:\n" + string.Join("\n", EmittedMetricNames(scrape)));

        failures.Should().BeEmpty(
            "every AlwaysOn tile's metric must be emitted by a basic turn — any miss means those " +
            "dashboard tiles are dark for everyone:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// Documents the confirmed broken tiles as a living record. Each KnownGap carries a reason;
    /// the list is logged so the count of silently-broken tiles is visible at a glance.
    /// </summary>
    [Fact]
    public void KnownGapTiles_AreDocumentedWithReasons()
    {
        var gaps = Tiles.Where(t => t.Value.Status == TileStatus.KnownGap).ToList();

        using var _ = new AssertionScope();
        foreach (var gap in gaps)
            gap.Value.Note.Should().NotBeNullOrWhiteSpace($"KnownGap tile '{gap.Key}' must document why it has no data");

        _output.WriteLine($"Dashboard KnownGap tiles ({gaps.Count}):\n" +
            string.Join("\n", gaps.OrderBy(g => g.Key).Select(g => $"  {g.Key} — {g.Value.Note}")));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunBasicTurnAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = "emission-test-agent",
            UserMessages = ["Hello from the dashboard emission test"],
            MaxTurns = 1,
        };

        var result = await mediator.Send(command, CancellationToken.None);
        result.Success.Should().BeTrue("the stub agent factory should return a valid response");
    }

    private async Task<string> ScrapeMetricsAsync()
    {
        _factory.Services.GetService<MeterProvider>()?.ForceFlush();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "dashboard-emission");

        var response = await client.GetAsync("/metrics");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static IEnumerable<string> EmittedMetricNames(string scrape) =>
        scrape.Split('\n')
            .Where(l => l.StartsWith("# TYPE ", StringComparison.Ordinal))
            .Select(l => l.Split(' ') is { Length: >= 4 } p ? p[2] : l)
            .Where(n => n.StartsWith("agent_", StringComparison.Ordinal) || n.StartsWith("rag_", StringComparison.Ordinal))
            .OrderBy(n => n);

    /// <summary>Reads the internal <c>MetricCatalog.Entries</c> (Id, Query) pairs via reflection.</summary>
    private static IReadOnlyList<(string Id, string Query)> GetCatalogEntries()
    {
        var hubAssembly = Assembly.GetAssembly(typeof(Program))
            ?? throw new InvalidOperationException("Could not load Presentation.AgentHub assembly via typeof(Program)");

        var catalogType = hubAssembly.GetTypes().SingleOrDefault(t => t.Name == "MetricCatalog")
            ?? throw new InvalidOperationException("MetricCatalog type not found");

        var entries = catalogType.GetField("Entries", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            as System.Collections.IEnumerable
            ?? throw new InvalidOperationException("MetricCatalog.Entries not found or not enumerable");

        var result = new List<(string, string)>();
        foreach (var entry in entries)
        {
            var type = entry.GetType();
            var id = type.GetProperty("Id")?.GetValue(entry) as string;
            var query = type.GetProperty("Query")?.GetValue(entry) as string;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(query))
                result.Add((id, query));
        }
        return result;
    }
}
