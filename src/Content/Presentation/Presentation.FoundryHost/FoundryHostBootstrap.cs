using Domain.AI.Agents;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Presentation.FoundryHost;

/// <summary>
/// Pure bootstrap logic for the Foundry hosted-agent container, factored out of
/// <see cref="Program"/> so the bug-prone parts — environment-variable translation, state-path
/// rooting, and agent/skill selection — are unit-testable without standing up the web host.
/// </summary>
internal static class FoundryHostBootstrap
{
    /// <summary>Default agent id served when <c>FOUNDRY_AGENT_ID</c> is not set.</summary>
    internal const string DefaultAgentId = "default";

    /// <summary>
    /// Subdirectory under the persistent <c>$HOME</c> where the harness's per-session local state
    /// lives. Foundry guarantees only <c>$HOME</c> (and <c>/files</c>) survive idle/resume, so all
    /// local-disk writes are rooted here rather than in the container's (non-persistent) app dir.
    /// </summary>
    internal const string StateRootSubdirectory = "agent-state";

    /// <summary>The variable Foundry injects with its Application Insights connection string.</summary>
    internal const string AppInsightsVariable = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>
    /// The deployer's opt-out from sending this host's telemetry to the Application Insights resource
    /// Foundry supplies (<c>AzureMonitorExporterConfig.EnableFromHostConnectionString</c>).
    /// </summary>
    internal const string EnableFromHostConnectionStringKey =
        "AppConfig:Observability:Exporters:AzureMonitor:EnableFromHostConnectionString";

    /// <summary>
    /// Whether the host uses Foundry's connection string, from the deployer's setting for
    /// <see cref="EnableFromHostConnectionStringKey"/> in the configuration the host loads. Where it can be set is
    /// described once, in <c>documentation/security/08-data-protection.html#foundry-telemetry-opt-out</c>.
    /// </summary>
    /// <param name="configured">The setting's raw value, or <c>null</c> when it is not set.</param>
    /// <returns>
    /// <see langword="false"/> only for an explicit <c>false</c>, so a typo cannot silently turn export off; the
    /// options binder rejects a value that is not a boolean when the configuration loads.
    /// </returns>
    internal static bool UsesHostConnectionString(string? configured) =>
        !bool.TryParse(configured, out var use) || use;

    /// <summary>
    /// Decides the deployer's telemetry choice from <paramref name="settings"/> and applies Foundry's environment
    /// accordingly, before the composition root loads configuration (#904).
    /// </summary>
    /// <param name="settings">
    /// The deployer's full configuration, Key Vault and App Configuration included, so the opt-out is honoured wherever
    /// it is kept.
    /// </param>
    /// <param name="readEnv">Reads an environment variable by name (returns <c>null</c> when unset).</param>
    /// <param name="writeEnv">Sets an environment variable; <c>null</c> removes it.</param>
    /// <returns>
    /// Whether Foundry supplied a connection string, and whether the host uses it: the inputs
    /// <see cref="DescribeTelemetryExport"/> needs once the host has bound its configuration.
    /// </returns>
    internal static (bool HostSupplied, bool UsedHostConnectionString) PrepareFoundryEnvironment(
        IConfiguration settings, Func<string, string?> readEnv, Action<string, string?> writeEnv)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var useHostConnectionString = UsesHostConnectionString(settings[EnableFromHostConnectionStringKey]);
        var hostSupplied = ApplyFoundryEnvironment(readEnv, writeEnv, useHostConnectionString);
        return (hostSupplied, useHostConnectionString);
    }

    /// <summary>
    /// Whether the harness's own Azure Monitor exporter is registered, by the same condition the telemetry configurator
    /// uses: <see cref="AzureMonitorExporterConfig.Enabled"/> and a non-empty
    /// <see cref="AzureMonitorExporterConfig.ConnectionString"/>.
    /// </summary>
    /// <param name="config">The bound exporter configuration.</param>
    /// <returns><see langword="true"/> when the exporter sends telemetry.</returns>
    internal static bool HarnessExporterIsActive(AzureMonitorExporterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Enabled && !string.IsNullOrWhiteSpace(config.ConnectionString);
    }

    /// <summary>
    /// Writes Foundry's environment onto the harness configuration keys, before the composition root
    /// loads configuration, and removes Foundry's connection string when the deployer opted out (#904).
    /// </summary>
    /// <param name="readEnv">Reads an environment variable by name (returns <c>null</c> when unset).</param>
    /// <param name="writeEnv">Sets an environment variable; <c>null</c> removes it.</param>
    /// <param name="useHostConnectionString">The deployer's choice, from <see cref="UsesHostConnectionString"/>.</param>
    /// <returns>Whether Foundry supplied a connection string.</returns>
    /// <remarks>
    /// <para>
    /// A value an operator set with the native <c>AppConfig__...</c> key is never overwritten.
    /// </para>
    /// <para>
    /// Removing the connection string is what makes the opt-out complete. The harness's Azure Monitor
    /// exporter sends only to the configured <c>ConnectionString</c>, which is then not mapped; but the
    /// Foundry hosting layer (<c>AgentHost</c>) exports its own traces and logs to Azure Monitor
    /// whenever the variable is set. With it gone, nothing is sent to Foundry's resource; the deployer
    /// can still point the harness's exporter at a resource of their own. Foundry's portal then shows
    /// no traces for this agent.
    /// </para>
    /// </remarks>
    internal static bool ApplyFoundryEnvironment(
        Func<string, string?> readEnv, Action<string, string?> writeEnv, bool useHostConnectionString)
    {
        ArgumentNullException.ThrowIfNull(readEnv);
        ArgumentNullException.ThrowIfNull(writeEnv);

        var hostSupplied = !string.IsNullOrWhiteSpace(readEnv(AppInsightsVariable));
        foreach (var (target, value) in BuildConfigOverrides(readEnv, useHostConnectionString))
        {
            if (string.IsNullOrEmpty(readEnv(target)))
                writeEnv(target, value);
        }

        if (hostSupplied && !useHostConnectionString)
            writeEnv(AppInsightsVariable, null);
        return hostSupplied;
    }

    /// <summary>
    /// Says at startup where this host's telemetry goes, from the configuration the host actually bound.
    /// </summary>
    /// <param name="hostSupplied">Whether Foundry supplied a connection string.</param>
    /// <param name="usedHostConnectionString">The deployer's choice, as applied before startup.</param>
    /// <param name="harnessExporterActive">
    /// Whether the harness's Azure Monitor exporter is registered, from <see cref="HarnessExporterIsActive"/> on the
    /// bound configuration.
    /// </param>
    /// <returns>The messages to log, each with its level; empty when there is nothing to say.</returns>
    internal static IReadOnlyList<(LogLevel Level, string Message)> DescribeTelemetryExport(
        bool hostSupplied, bool usedHostConnectionString, bool harnessExporterActive)
    {
        var notices = new List<(LogLevel, string)>();
        var harness = harnessExporterActive ? "on" : "off";
        if (hostSupplied && usedHostConnectionString)
            notices.Add((LogLevel.Information,
                $"Foundry supplied {AppInsightsVariable}, so the Foundry hosting layer exports its traces and logs to that " +
                $"Application Insights resource, and the harness's Azure Monitor exporter is {harness}. Set " +
                $"{EnableFromHostConnectionStringKey} to false to send nothing there; each configuration source spells " +
                "the name differently (see documentation/security/08-data-protection.html#foundry-telemetry-opt-out)."));
        else if (hostSupplied)
            notices.Add((LogLevel.Information,
                $"{EnableFromHostConnectionStringKey} is false, so FoundryHost removed Foundry's {AppInsightsVariable}: " +
                "nothing is exported to Foundry's Application Insights resource. The harness's Azure Monitor exporter is " +
                $"{harness} (AppConfig:Observability:Exporters:AzureMonitor)."));
        return notices;
    }

    /// <summary>
    /// Resolves the directory the agent session store persists the framework's runtime state to:
    /// <c>{HOME}/.checkpoints</c>, falling back to the working directory when <c>HOME</c> is unset.
    /// <c>$HOME</c> is the one directory the Foundry platform keeps writable and durable for a
    /// session's lifetime.
    /// </summary>
    /// <param name="readEnv">Reads an environment variable by name (returns <c>null</c> when unset).</param>
    /// <param name="currentDirectory">The working directory to fall back to.</param>
    internal static string ResolveSessionStoreRoot(Func<string, string?> readEnv, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(readEnv);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var home = readEnv("HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(home) ? currentDirectory : home, ".checkpoints");
    }

    /// <summary>
    /// Computes the harness configuration overrides implied by Foundry's runtime-injected
    /// environment variables. Returns the <c>AppConfig__...</c> keys that should be set, leaving the
    /// actual <see cref="Environment"/> mutation to the caller so this stays pure and testable.
    /// </summary>
    /// <param name="readEnv">Reads an environment variable by name (returns <c>null</c> when unset).</param>
    /// <remarks>
    /// <para>Three groups of overrides are produced:</para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Inference + telemetry</b> — Foundry's injected <c>FOUNDRY_PROJECT_ENDPOINT</c>,
    ///     <c>AZURE_AI_MODEL_DEPLOYMENT_NAME</c>, and <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Per-session local state → <c>$HOME</c></b> — the planner database, audit trails, and
    ///     logs are re-rooted under the persistent <c>$HOME</c> so they survive Foundry's
    ///     idle/resume cycle (the container's publish directory is <i>not</i> persisted). This is the
    ///     core of the hosted-runtime profile and requires no external services.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Optional external stores</b> — the graph/knowledge backend, RAG vector store, and
    ///     distributed cache each promote themselves from their in-process default to an external
    ///     managed service ONLY when the matching connection environment variable is supplied. With
    ///     none supplied the container boots standalone (durable per-conversation memory via
    ///     <c>$HOME</c>); external stores are needed only to share knowledge across conversations or
    ///     beyond the session retention window.
    ///   </description></item>
    /// </list>
    /// Only non-empty source values produce overrides. A present
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> additionally enables the Azure Monitor exporter
    /// so harness traces and metrics flow to the injected sink, unless the deployer set
    /// <see cref="EnableFromHostConnectionStringKey"/> to <c>false</c>, in which case neither is
    /// mapped (#904).
    /// </remarks>
    /// <param name="useHostConnectionString">
    /// Whether to use Foundry's connection string; see <see cref="ApplyFoundryEnvironment"/>.
    /// </param>
    internal static IReadOnlyDictionary<string, string> BuildConfigOverrides(
        Func<string, string?> readEnv, bool useHostConnectionString = true)
    {
        ArgumentNullException.ThrowIfNull(readEnv);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        AddIfPresent(overrides, readEnv, "FOUNDRY_PROJECT_ENDPOINT", "AppConfig__AI__AIFoundry__ProjectEndpoint");
        AddIfPresent(overrides, readEnv, "AZURE_AI_MODEL_DEPLOYMENT_NAME", "AppConfig__AI__AgentFramework__DefaultDeployment");

        var appInsights = readEnv(AppInsightsVariable);
        if (useHostConnectionString && !string.IsNullOrWhiteSpace(appInsights))
        {
            overrides["AppConfig__Observability__Exporters__AzureMonitor__ConnectionString"] = appInsights;
            overrides["AppConfig__Observability__Exporters__AzureMonitor__Enabled"] = "true";
        }

        AddHomeRootedStatePaths(overrides, readEnv);
        AddExternalStorePromotions(overrides, readEnv);

        return overrides;
    }

    /// <summary>
    /// Roots every local-disk store under the persistent <c>$HOME</c> so the harness's state
    /// survives Foundry's idle/resume cycle. No-op when <c>HOME</c> is unset (non-Foundry runs),
    /// where the harness's existing relative defaults apply.
    /// </summary>
    /// <remarks>
    /// Each resolver in the harness already honors an absolute path (it either combines via
    /// <see cref="Path.Combine(string, string)"/>, which discards the base when the second argument
    /// is rooted, or uses the configured value verbatim) — none expand <c>$HOME</c>. Resolving the
    /// absolute paths here is therefore the minimal, shared-code-free way to land them in the
    /// persistent location. Paths are joined with <c>/</c> because the hosted runtime is Linux,
    /// independent of the OS this code is built or unit-tested on.
    /// </remarks>
    private static void AddHomeRootedStatePaths(
        IDictionary<string, string> overrides, Func<string, string?> readEnv)
    {
        var home = readEnv("HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            return;
        }

        var stateRoot = $"{home.TrimEnd('/')}/{StateRootSubdirectory}";

        // Planner checkpoint/resume database (SQLite).
        overrides["AppConfig__AI__Planner__DatabasePath"] = $"{stateRoot}/planner/planner.db";

        // JSONL audit trails — drift, governance decisions, governance escalations, change proposals
        // (+ gate evidence), egress decisions, and the compliance record of erasures, exports and
        // document removals. Each is append-only forensic state worth preserving across resumes.
        overrides["AppConfig__AI__DriftDetection__AuditPath"] = $"{stateRoot}/audit";
        overrides["AppConfig__AI__Governance__AuditStoragePath"] = $"{stateRoot}/governance";
        overrides["AppConfig__AI__Governance__Escalation__AuditStoragePath"] = $"{stateRoot}/escalations";
        overrides["AppConfig__AI__Changes__AuditStoragePath"] = $"{stateRoot}/changes";
        overrides["AppConfig__AI__Changes__EvidenceStoragePath"] = $"{stateRoot}/changes/evidence";
        overrides["AppConfig__AI__Egress__AuditStoragePath"] = $"{stateRoot}/egress";
        overrides["AppConfig__AI__Erasure__AuditStoragePath"] = $"{stateRoot}/compliance";

        // Per-session agent working state that also defaults under .agent-sessions/: offloaded tool
        // results (large outputs spilled to disk), subagent delegation records, and the inter-agent
        // mailbox. StoragePath is the .agent-sessions root itself (the store appends
        // {scope folder}/tool-results/), so it maps to the state root directly. Mailbox has no writer
        // today but is rooted now so the capability lands persistent when it ships.
        overrides["AppConfig__AI__ContextManagement__ToolResultStorage__StoragePath"] = stateRoot;
        overrides["AppConfig__AI__Orchestration__Subagent__DelegationStoragePath"] = $"{stateRoot}/delegations";
        overrides["AppConfig__AI__Orchestration__Subagent__MailboxStoragePath"] = $"{stateRoot}/mailbox";

        // Embedded graph database (Kuzu) — the default GraphDatabase.Provider, so it writes to local
        // disk out of the box. (External Neo4j/PostgreSQL promotion targets a different subsystem,
        // the GraphRag knowledge store, so this still applies when the embedded backend is in use.)
        overrides["AppConfig__AI__Rag__GraphDatabase__DataDirectory"] = $"{stateRoot}/graph";

        // File logs.
        overrides["AppConfig__Logging__LogsBasePath"] = $"{stateRoot}/logs";
    }

    /// <summary>
    /// Promotes the graph/knowledge backend, RAG vector store, and distributed cache from their
    /// in-process defaults to external managed services — but only for each subsystem whose
    /// connection environment variable is present. Subsystems with no env var keep their standalone
    /// default, so the container always boots.
    /// </summary>
    /// <remarks>
    /// A malformed connection still fails loudly at startup (correct: that is operator
    /// misconfiguration, not a missing opt-in). Connection secrets are read from env here only so
    /// Foundry/Key Vault can inject them; the harness never persists them to appsettings.
    /// </remarks>
    private static void AddExternalStorePromotions(
        IDictionary<string, string> overrides, Func<string, string?> readEnv)
    {
        // Graph + cross-session knowledge store (Neo4j or PostgreSQL). GRAPH_PROVIDER selects the
        // backend keyed in Infrastructure.AI.KnowledgeGraph DI; the connection string carries the
        // endpoint/credentials. Promoting the backend also enables GraphRAG retrieval, which is
        // off by default — an operator who wires a graph DB wants it actually used, not just
        // connected.
        var graphProvider = readEnv("GRAPH_PROVIDER");
        var graphConnection = readEnv("GRAPH_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(graphProvider) && !string.IsNullOrWhiteSpace(graphConnection))
        {
            var normalizedProvider = graphProvider.Trim().ToLowerInvariant();
            if (normalizedProvider is not ("neo4j" or "postgresql"))
            {
                throw new InvalidOperationException(
                    $"GRAPH_PROVIDER must be 'neo4j' or 'postgresql' (got '{graphProvider}'). " +
                    "These are the external graph backends registered for hosted agents; leave " +
                    "GRAPH_PROVIDER unset to use the in-process graph store.");
            }

            overrides["AppConfig__AI__Rag__GraphRag__GraphProvider"] = normalizedProvider;
            overrides["AppConfig__AI__Rag__GraphRag__ConnectionString"] = graphConnection;
            overrides["AppConfig__AI__Rag__GraphRag__Enabled"] = "true";
        }

        // RAG vector + BM25 store (Azure AI Search). Endpoint presence is the opt-in; the API key is
        // optional because the agent's managed identity is the preferred auth path. With no
        // endpoint, force the in-process FAISS/FTS5 store: the vector store's config default is
        // Azure AI Search, which would fail without an endpoint, so this keeps the container
        // bootable standalone (RAG state is per-session, rebuilt from ingested documents). The
        // fallback defers to an operator who configured a vector store via the native AppConfig__
        // keys, so we never silently override a hand-configured endpoint with FAISS.
        var searchEndpoint = readEnv("AZURE_SEARCH_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(searchEndpoint))
        {
            overrides["AppConfig__AI__Rag__VectorStore__Provider"] = "azure_ai_search";
            overrides["AppConfig__AI__Rag__VectorStore__Endpoint"] = searchEndpoint;
            AddIfPresent(overrides, readEnv, "AZURE_SEARCH_API_KEY", "AppConfig__AI__Rag__VectorStore__ApiKey");
            AddIfPresent(overrides, readEnv, "AZURE_SEARCH_INDEX", "AppConfig__AI__Rag__VectorStore__IndexName");
        }
        else if (string.IsNullOrWhiteSpace(readEnv("AppConfig__AI__Rag__VectorStore__Provider"))
            && string.IsNullOrWhiteSpace(readEnv("AppConfig__AI__Rag__VectorStore__Endpoint")))
        {
            overrides["AppConfig__AI__Rag__VectorStore__Provider"] = "faiss";
        }

        // Distributed cache (Redis) — shared across sessions/replicas.
        var redisEndpoint = readEnv("REDIS_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(redisEndpoint))
        {
            overrides["AppConfig__Cache__CacheType"] = "RedisCache";
            overrides["AppConfig__Cache__RedisClient__Endpoint"] = redisEndpoint;
            AddIfPresent(overrides, readEnv, "REDIS_SECRET", "AppConfig__Cache__RedisClient__Secret");
        }
    }

    /// <summary>
    /// Resolves which agent id this deployment exposes: <c>FOUNDRY_AGENT_ID</c> when set,
    /// otherwise <see cref="DefaultAgentId"/>.
    /// </summary>
    internal static string ResolveAgentId(Func<string, string?> readEnv)
    {
        ArgumentNullException.ThrowIfNull(readEnv);
        var configured = readEnv("FOUNDRY_AGENT_ID");
        return string.IsNullOrWhiteSpace(configured) ? DefaultAgentId : configured;
    }

    /// <summary>
    /// Returns the skill ids that compose an agent: the manifest's declared skills, or the agent id
    /// itself as a single skill when the manifest declares none (per <see cref="AgentDefinition"/>).
    /// </summary>
    internal static IReadOnlyList<string> ResolveSkillIds(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Skills.Count > 0 ? definition.Skills : [definition.Id];
    }

    private static void AddIfPresent(
        IDictionary<string, string> overrides,
        Func<string, string?> readEnv,
        string source,
        string target)
    {
        var value = readEnv(source);
        if (!string.IsNullOrWhiteSpace(value))
        {
            overrides[target] = value;
        }
    }
}
