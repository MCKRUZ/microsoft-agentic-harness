diff --git a/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs b/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs
index f357ca3..670c87a 100644
--- a/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs
+++ b/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs
@@ -2,10 +2,12 @@ using Application.AI.Common.Helpers;
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.Context;
 using Application.AI.Common.Interfaces.Tools;
+using Application.AI.Common.Interfaces.Traces;
 using Domain.AI.Agents;
 using Domain.AI.Skills;
 using Domain.Common.Config;
 using Domain.Common.Config.AI;
+using Domain.Common.MetaHarness;
 using Microsoft.Agents.AI;
 using Microsoft.Extensions.AI;
 using Microsoft.Extensions.DependencyInjection;
@@ -28,6 +30,7 @@ public class AgentExecutionContextFactory
 	private readonly IToolConverter? _toolConverter;
 	private readonly IMcpToolProvider? _mcpToolProvider;
 	private readonly IContextBudgetTracker? _budgetTracker;
+	private readonly IExecutionTraceStore? _traceStore;
 
 	public AgentExecutionContextFactory(
 		ILogger<AgentExecutionContextFactory> logger,
@@ -36,7 +39,8 @@ public class AgentExecutionContextFactory
 		ILoggerFactory loggerFactory,
 		IToolConverter? toolConverter = null,
 		IMcpToolProvider? mcpToolProvider = null,
-		IContextBudgetTracker? budgetTracker = null)
+		IContextBudgetTracker? budgetTracker = null,
+		IExecutionTraceStore? traceStore = null)
 	{
 		_logger = logger;
 		_appConfig = appConfig;
@@ -45,11 +49,14 @@ public class AgentExecutionContextFactory
 		_toolConverter = toolConverter;
 		_mcpToolProvider = mcpToolProvider;
 		_budgetTracker = budgetTracker;
+		_traceStore = traceStore;
 	}
 
 	/// <summary>
 	/// Maps a skill definition and options to a runtime agent execution context.
 	/// Wires <see cref="FileAgentSkillsProvider"/> for progressive skill disclosure.
+	/// When an <see cref="IExecutionTraceStore"/> is available, starts a trace run and
+	/// stores the resulting <see cref="ITraceWriter"/> in <c>AdditionalProperties["__traceWriter"]</c>.
 	/// </summary>
 	public async Task<AgentExecutionContext> MapToAgentContextAsync(SkillDefinition skill, SkillAgentOptions options)
 	{
@@ -63,6 +70,9 @@ public class AgentExecutionContextFactory
 			?? _appConfig.CurrentValue.AI?.AgentFramework?.ClientType
 			?? AIAgentFrameworkClientType.AzureOpenAI;
 
+		// Resolve or create a trace scope for this execution
+		var traceScope = options.TraceScope ?? TraceScope.ForExecution(Guid.NewGuid());
+
 		// Track context budget allocations
 		if (_budgetTracker != null)
 		{
@@ -76,6 +86,28 @@ public class AgentExecutionContextFactory
 			}
 		}
 
+		var additionalProps = BuildAdditionalProperties(skill, options);
+
+		// Start a trace run when a store is wired in
+		if (_traceStore != null)
+		{
+			var metadata = new RunMetadata
+			{
+				AgentName = agentName,
+				StartedAt = DateTimeOffset.UtcNow
+			};
+			var traceWriter = await _traceStore.StartRunAsync(traceScope, metadata);
+			additionalProps["__traceWriter"] = traceWriter;
+
+			// Set candidate baggage on the current Activity for CausalSpanAttributionProcessor
+			if (traceScope.CandidateId.HasValue)
+			{
+				System.Diagnostics.Activity.Current?.AddBaggage(
+					Domain.AI.Telemetry.Conventions.ToolConventions.HarnessCandidateId,
+					traceScope.CandidateId.Value.ToString("D"));
+			}
+		}
+
 		var context = new AgentExecutionContext
 		{
 			Name = agentName,
@@ -87,7 +119,8 @@ public class AgentExecutionContextFactory
 			Tools = tools,
 			AIContextProviders = aiContextProviders,
 			MiddlewareTypes = middlewareTypes,
-			AdditionalProperties = BuildAdditionalProperties(skill, options)
+			TraceScope = traceScope,
+			AdditionalProperties = additionalProps
 		};
 
 		_logger.LogInformation(
diff --git a/src/Content/Application/Application.AI.Common/Middleware/ToolDiagnosticsMiddleware.cs b/src/Content/Application/Application.AI.Common/Middleware/ToolDiagnosticsMiddleware.cs
index 248b24b..305a659 100644
--- a/src/Content/Application/Application.AI.Common/Middleware/ToolDiagnosticsMiddleware.cs
+++ b/src/Content/Application/Application.AI.Common/Middleware/ToolDiagnosticsMiddleware.cs
@@ -1,4 +1,7 @@
+using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.Traces;
 using Domain.Common.Extensions;
+using Domain.Common.MetaHarness;
 using Microsoft.Extensions.AI;
 using Microsoft.Extensions.Logging;
 using System.Runtime.CompilerServices;
@@ -17,19 +20,30 @@ public sealed class ToolDiagnosticsMiddleware : DelegatingChatClient
 {
     private const int MaxToolsToLog = 5;
     private const int MaxPreviewLength = 200;
+    private const int MaxPayloadSummaryLength = 500;
 
     private readonly ILogger _logger;
+    private readonly ITraceWriter? _traceWriter;
+    private readonly ISecretRedactor? _redactor;
 
     /// <summary>
     /// Initializes a new instance of the <see cref="ToolDiagnosticsMiddleware"/> class.
     /// </summary>
     /// <param name="innerClient">The inner chat client to wrap with diagnostics.</param>
     /// <param name="logger">Logger for recording tool diagnostic events.</param>
-    public ToolDiagnosticsMiddleware(IChatClient innerClient, ILogger<ToolDiagnosticsMiddleware> logger)
+    /// <param name="traceWriter">Optional trace writer for recording tool result events.</param>
+    /// <param name="redactor">Optional secret redactor applied to payloads before tracing.</param>
+    public ToolDiagnosticsMiddleware(
+        IChatClient innerClient,
+        ILogger<ToolDiagnosticsMiddleware> logger,
+        ITraceWriter? traceWriter = null,
+        ISecretRedactor? redactor = null)
         : base(innerClient)
     {
         ArgumentNullException.ThrowIfNull(logger);
         _logger = logger;
+        _traceWriter = traceWriter;
+        _redactor = redactor;
     }
 
     /// <inheritdoc />
@@ -42,6 +56,10 @@ public sealed class ToolDiagnosticsMiddleware : DelegatingChatClient
         var toolsWereConfigured = options?.Tools is { Count: > 0 };
         LogToolsInOptions(options, nameof(GetResponseAsync));
 
+        // Trace any function results being submitted to the LLM (i.e., tool calls that completed)
+        if (_traceWriter != null)
+            await AppendFunctionResultTracesAsync(messages, cancellationToken);
+
         try
         {
             var response = await base.GetResponseAsync(messages, options, cancellationToken);
@@ -57,6 +75,42 @@ public sealed class ToolDiagnosticsMiddleware : DelegatingChatClient
         }
     }
 
+    private async Task AppendFunctionResultTracesAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
+    {
+        var functionResults = messages
+            .SelectMany(m => m.Contents)
+            .OfType<FunctionResultContent>()
+            .ToList();
+
+        foreach (var result in functionResults)
+        {
+            try
+            {
+                var rawPayload = result.Result?.ToString() ?? string.Empty;
+                var payload = _redactor?.Redact(rawPayload) ?? rawPayload;
+                if (payload.Length > MaxPayloadSummaryLength)
+                    payload = payload[..MaxPayloadSummaryLength];
+
+                var record = new ExecutionTraceRecord
+                {
+                    Ts = DateTimeOffset.UtcNow,
+                    Type = TraceRecordTypes.ToolResult,
+                    ExecutionRunId = _traceWriter!.Scope.ExecutionRunId.ToString("D"),
+                    TurnId = result.CallId ?? Guid.NewGuid().ToString("D"),
+                    ResultCategory = TraceResultCategories.Success,
+                    PayloadSummary = payload
+                };
+
+                await _traceWriter!.AppendTraceAsync(record, ct);
+            }
+            catch (Exception ex)
+            {
+                _logger.LogWarning(ex,
+                    "[ToolDiag] Failed to append trace record for CallId={CallId}", result.CallId);
+            }
+        }
+    }
+
     // Deduplicate tools by name (case-insensitive) before they reach the HTTP layer.
     // The framework merges ChatOptions.Tools + AIContext.Tools from providers, which can
     // produce duplicates that the Anthropic API rejects with "Tool names must be unique".
diff --git a/src/Content/Domain/Domain.AI/Agents/AgentExecutionContext.cs b/src/Content/Domain/Domain.AI/Agents/AgentExecutionContext.cs
index 111b655..a208e75 100644
--- a/src/Content/Domain/Domain.AI/Agents/AgentExecutionContext.cs
+++ b/src/Content/Domain/Domain.AI/Agents/AgentExecutionContext.cs
@@ -1,4 +1,5 @@
 using Domain.Common.Config.AI;
+using Domain.Common.MetaHarness;
 using Microsoft.Agents.AI;
 using Microsoft.Extensions.AI;
 
@@ -68,6 +69,12 @@ public class AgentExecutionContext
 	/// </summary>
 	public IList<AIContextProvider>? AIContextProviders { get; set; }
 
+	/// <summary>
+	/// Trace scope for this execution run. Set by <c>AgentExecutionContextFactory</c>
+	/// when an <c>IExecutionTraceStore</c> is wired in.
+	/// </summary>
+	public TraceScope? TraceScope { get; set; }
+
 	/// <summary>
 	/// Extensible configuration properties.
 	/// </summary>
diff --git a/src/Content/Domain/Domain.AI/Skills/SkillAgentOptions.cs b/src/Content/Domain/Domain.AI/Skills/SkillAgentOptions.cs
index 9c9363d..05d19b3 100644
--- a/src/Content/Domain/Domain.AI/Skills/SkillAgentOptions.cs
+++ b/src/Content/Domain/Domain.AI/Skills/SkillAgentOptions.cs
@@ -1,4 +1,5 @@
 using Domain.Common.Config.AI;
+using Domain.Common.MetaHarness;
 using Microsoft.Extensions.AI;
 
 namespace Domain.AI.Skills;
@@ -95,5 +96,11 @@ public class SkillAgentOptions
 	/// </summary>
 	public IDictionary<string, object>? AdditionalProperties { get; set; }
 
+	/// <summary>
+	/// Optional trace scope for this run. When set, the factory uses this scope;
+	/// otherwise <c>TraceScope.ForExecution(Guid.NewGuid())</c> is created.
+	/// </summary>
+	public TraceScope? TraceScope { get; set; }
+
 	#endregion
 }
diff --git a/src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs b/src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs
index 5de3212..241ef1c 100644
--- a/src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs
+++ b/src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs
@@ -44,4 +44,17 @@ public static class ToolConventions
     public const string EmptyResults = "agent.tool.empty_results";
     /// <summary>Histogram: tool result size in characters.</summary>
     public const string ResultSize = "agent.tool.result_size";
+
+    // Causal attribution attributes (Meta-Harness OTel GenAI semantic conventions)
+
+    /// <summary>OTel GenAI semantic convention attribute for tool name (bridged from agent.tool.name).</summary>
+    public const string GenAiToolName = "gen_ai.tool.name";
+    /// <summary>SHA256 hex digest of serialized tool input. Only set when IsAllDataRequested.</summary>
+    public const string InputHash = "tool.input_hash";
+    /// <summary>Bucketed outcome category matching ExecutionTraceRecord.result_category.</summary>
+    public const string ResultCategory = "tool.result_category";
+    /// <summary>CandidateId from TraceScope when running inside an optimization eval.</summary>
+    public const string HarnessCandidateId = "gen_ai.harness.candidate_id";
+    /// <summary>Iteration number from TraceScope when running inside an optimization eval.</summary>
+    public const string HarnessIteration = "gen_ai.harness.iteration";
 }
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index 4ac0ef9..c1f05bd 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -1,6 +1,8 @@
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.A2A;
+using Application.AI.Common.Interfaces.Traces;
 using Infrastructure.AI.Security;
+using Infrastructure.AI.Traces;
 using Application.AI.Common.Interfaces.Agent;
 using Application.AI.Common.Interfaces.Agents;
 using Application.AI.Common.Interfaces.Compaction;
@@ -69,6 +71,9 @@ public static class DependencyInjection
         // Secret redaction — applied at all persistence boundaries (traces, snapshots, manifests)
         services.AddSingleton<ISecretRedactor, PatternSecretRedactor>();
 
+        // Execution trace store — filesystem-backed per-run trace artifact persistence
+        services.AddSingleton<IExecutionTraceStore, FileSystemExecutionTraceStore>();
+
         // AI client registration — AzureOpenAIClient or OpenAIClient based on config
         RegisterAIClients(services, appConfig);
 
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Traces/FileSystemExecutionTraceStore.cs b/src/Content/Infrastructure/Infrastructure.AI/Traces/FileSystemExecutionTraceStore.cs
new file mode 100644
index 0000000..a03bf97
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Traces/FileSystemExecutionTraceStore.cs
@@ -0,0 +1,229 @@
+using System.Text;
+using System.Text.Json;
+using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.Traces;
+using Domain.Common.Config;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.Traces;
+
+/// <summary>
+/// Filesystem-backed implementation of <see cref="IExecutionTraceStore"/>.
+/// Creates one directory per execution run under <c>MetaHarnessConfig.TraceDirectoryRoot</c>
+/// and returns a scoped <see cref="ITraceWriter"/> for writing trace artifacts.
+/// </summary>
+public sealed class FileSystemExecutionTraceStore : IExecutionTraceStore
+{
+    private readonly IOptions<AppConfig> _appConfig;
+    private readonly ISecretRedactor _redactor;
+    private readonly ILogger<FileSystemExecutionTraceStore> _logger;
+
+    private static readonly JsonSerializerOptions JsonOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
+        WriteIndented = false
+    };
+
+    /// <summary>
+    /// Initializes a new instance of <see cref="FileSystemExecutionTraceStore"/>.
+    /// </summary>
+    public FileSystemExecutionTraceStore(
+        IOptions<AppConfig> appConfig,
+        ISecretRedactor redactor,
+        ILogger<FileSystemExecutionTraceStore> logger)
+    {
+        _appConfig = appConfig;
+        _redactor = redactor;
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public async Task<ITraceWriter> StartRunAsync(TraceScope scope, RunMetadata metadata, CancellationToken ct = default)
+    {
+        var config = _appConfig.Value.MetaHarness;
+        var dir = scope.ResolveDirectory(config.TraceDirectoryRoot);
+        Directory.CreateDirectory(dir);
+
+        var manifest = new
+        {
+            execution_run_id = scope.ExecutionRunId.ToString("D"),
+            agent_name = metadata.AgentName,
+            started_at = metadata.StartedAt.ToString("O"),
+            write_completed = false
+        };
+        await WriteAtomicAsync(
+            Path.Combine(dir, "manifest.json"),
+            JsonSerializer.Serialize(manifest));
+
+        _logger.LogDebug(
+            "Started trace run {RunId} in {Dir}",
+            scope.ExecutionRunId, dir);
+
+        return new FileSystemTraceWriter(dir, scope, _redactor, config);
+    }
+
+    /// <inheritdoc />
+    public Task<string> GetRunDirectoryAsync(TraceScope scope, CancellationToken ct = default)
+    {
+        var root = _appConfig.Value.MetaHarness.TraceDirectoryRoot;
+        return Task.FromResult(scope.ResolveDirectory(root));
+    }
+
+    private static async Task WriteAtomicAsync(string targetPath, string content)
+    {
+        var tmp = targetPath + ".tmp";
+        await File.WriteAllTextAsync(tmp, content);
+        File.Move(tmp, targetPath, overwrite: true);
+    }
+
+    // -------------------------------------------------------------------------
+    // FileSystemTraceWriter — scoped writer for one execution run
+    // -------------------------------------------------------------------------
+
+    private sealed class FileSystemTraceWriter : ITraceWriter
+    {
+        private readonly ISecretRedactor _redactor;
+        private readonly MetaHarnessConfig _config;
+        private readonly string _executionRunId;
+        private readonly string _agentName;
+        private readonly string _startedAt;
+
+        private long _sequenceCounter;
+        private readonly SemaphoreSlim _tracesLock = new(1, 1);
+
+        private static readonly JsonSerializerOptions JsonOptions = new()
+        {
+            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
+            WriteIndented = false
+        };
+
+        public TraceScope Scope { get; }
+        public string RunDirectory { get; }
+
+        public FileSystemTraceWriter(
+            string runDirectory,
+            TraceScope scope,
+            ISecretRedactor redactor,
+            MetaHarnessConfig config)
+        {
+            RunDirectory = runDirectory;
+            Scope = scope;
+            _redactor = redactor;
+            _config = config;
+            _executionRunId = scope.ExecutionRunId.ToString("D");
+            _agentName = string.Empty; // stored in manifest only
+            _startedAt = string.Empty; // stored in manifest only
+        }
+
+        public async Task WriteTurnAsync(int turnNumber, TurnArtifacts artifacts, CancellationToken ct = default)
+        {
+            var turnDir = Path.Combine(RunDirectory, "turns", turnNumber.ToString());
+            Directory.CreateDirectory(turnDir);
+
+            if (artifacts.SystemPrompt is { } prompt)
+            {
+                var redacted = _redactor.Redact(prompt) ?? string.Empty;
+                await File.WriteAllTextAsync(Path.Combine(turnDir, "system_prompt.md"), redacted, ct);
+            }
+
+            if (artifacts.ToolCallsJsonl is { } calls)
+                await File.WriteAllTextAsync(Path.Combine(turnDir, "tool_calls.jsonl"), calls, ct);
+
+            if (artifacts.ModelResponse is { } response)
+                await File.WriteAllTextAsync(Path.Combine(turnDir, "model_response.md"), response, ct);
+
+            if (artifacts.StateSnapshot is { } snapshot)
+                await File.WriteAllTextAsync(Path.Combine(turnDir, "state_snapshot.json"), snapshot, ct);
+
+            if (artifacts.ToolResults.Count > 0)
+            {
+                var maxBytes = _config.MaxFullPayloadKB * 1024;
+                var toolResultsDir = Path.Combine(turnDir, "tool_results");
+
+                foreach (var (callId, result) in artifacts.ToolResults)
+                {
+                    if (Encoding.UTF8.GetByteCount(result) > maxBytes)
+                    {
+                        Directory.CreateDirectory(toolResultsDir);
+                        await File.WriteAllTextAsync(
+                            Path.Combine(toolResultsDir, $"{callId}.json"), result, ct);
+                    }
+                }
+            }
+        }
+
+        public async Task AppendTraceAsync(ExecutionTraceRecord record, CancellationToken ct = default)
+        {
+            var seq = Interlocked.Increment(ref _sequenceCounter);
+
+            var redactedSummary = _redactor.Redact(record.PayloadSummary);
+            if (redactedSummary?.Length > 500)
+                redactedSummary = redactedSummary[..500];
+
+            var finalRecord = record with
+            {
+                Seq = seq,
+                Ts = record.Ts == default ? DateTimeOffset.UtcNow : record.Ts,
+                PayloadSummary = redactedSummary
+            };
+
+            var line = JsonSerializer.Serialize(finalRecord, JsonOptions) + "\n";
+
+            await _tracesLock.WaitAsync(ct);
+            try
+            {
+                await File.AppendAllTextAsync(
+                    Path.Combine(RunDirectory, "traces.jsonl"), line, ct);
+            }
+            finally
+            {
+                _tracesLock.Release();
+            }
+        }
+
+        public async Task WriteScoresAsync(HarnessScores scores, CancellationToken ct = default)
+        {
+            var json = JsonSerializer.Serialize(scores, JsonOptions);
+            await WriteAtomicAsync(Path.Combine(RunDirectory, "scores.json"), json);
+        }
+
+        public async Task WriteSummaryAsync(string markdown, CancellationToken ct = default)
+        {
+            await WriteAtomicAsync(Path.Combine(RunDirectory, "summary.md"), markdown);
+        }
+
+        public async Task CompleteAsync(CancellationToken ct = default)
+        {
+            var manifestPath = Path.Combine(RunDirectory, "manifest.json");
+            var existing = await File.ReadAllTextAsync(manifestPath, ct);
+
+            // Parse existing manifest and update write_completed flag
+            using var doc = JsonDocument.Parse(existing);
+            var props = new Dictionary<string, object?>();
+            foreach (var prop in doc.RootElement.EnumerateObject())
+            {
+                props[prop.Name] = prop.Value.ValueKind switch
+                {
+                    JsonValueKind.String => (object?)prop.Value.GetString(),
+                    JsonValueKind.True => true,
+                    JsonValueKind.False => false,
+                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
+                    _ => prop.Value.GetRawText()
+                };
+            }
+            props["write_completed"] = true;
+
+            await WriteAtomicAsync(manifestPath, JsonSerializer.Serialize(props));
+        }
+
+        private static async Task WriteAtomicAsync(string targetPath, string content)
+        {
+            var tmp = targetPath + ".tmp";
+            await File.WriteAllTextAsync(tmp, content);
+            File.Move(tmp, targetPath, overwrite: true);
+        }
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.Observability/Exporters/ObservabilityTelemetryConfigurator.cs b/src/Content/Infrastructure/Infrastructure.Observability/Exporters/ObservabilityTelemetryConfigurator.cs
index bfda99e..b55f3ff 100644
--- a/src/Content/Infrastructure/Infrastructure.Observability/Exporters/ObservabilityTelemetryConfigurator.cs
+++ b/src/Content/Infrastructure/Infrastructure.Observability/Exporters/ObservabilityTelemetryConfigurator.cs
@@ -96,7 +96,13 @@ public sealed class ObservabilityTelemetryConfigurator : ITelemetryConfigurator
             _loggerFactory.CreateLogger<ToolEffectivenessProcessor>()));
         _logger.LogInformation("Tool effectiveness processor registered");
 
-        // Processor 5: Tail-based sampling (last — all metrics already recorded)
+        // Processor 5: Causal attribution — bridges agent.tool.name → gen_ai.tool.name,
+        // adds input hash and result category, reads eval context from baggage
+        builder.AddProcessor(new CausalSpanAttributionProcessor(
+            _loggerFactory.CreateLogger<CausalSpanAttributionProcessor>()));
+        _logger.LogInformation("Causal span attribution processor registered");
+
+        // Processor 6: Tail-based sampling (last — all metrics already recorded)
         if (config.Sampling.Enabled)
         {
             builder.AddProcessor(new TailBasedSamplingProcessor(
diff --git a/src/Content/Infrastructure/Infrastructure.Observability/Processors/CausalSpanAttributionProcessor.cs b/src/Content/Infrastructure/Infrastructure.Observability/Processors/CausalSpanAttributionProcessor.cs
new file mode 100644
index 0000000..4c4112a
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.Observability/Processors/CausalSpanAttributionProcessor.cs
@@ -0,0 +1,84 @@
+using System.Diagnostics;
+using System.Security.Cryptography;
+using System.Text;
+using Domain.AI.Telemetry.Conventions;
+using Domain.Common.MetaHarness;
+using Microsoft.Extensions.Logging;
+using OpenTelemetry;
+
+namespace Infrastructure.Observability.Processors;
+
+/// <summary>
+/// Span processor that enriches <c>execute_tool</c> spans with causal attribution
+/// attributes following the OTel GenAI semantic conventions.
+/// </summary>
+/// <remarks>
+/// <para>Runs after <see cref="ToolEffectivenessProcessor"/> in the pipeline.</para>
+/// <para>Attributes added to tool spans:</para>
+/// <list type="bullet">
+///   <item><description><c>gen_ai.tool.name</c> — bridged from <c>agent.tool.name</c></description></item>
+///   <item><description><c>tool.input_hash</c> — SHA-256 of the tool result tag (only when <c>IsAllDataRequested</c>)</description></item>
+///   <item><description><c>tool.result_category</c> — bucketed outcome from span status</description></item>
+///   <item><description><c>gen_ai.harness.candidate_id</c> — from Activity baggage when in an eval context</description></item>
+///   <item><description><c>gen_ai.harness.iteration</c> — from Activity baggage when in an eval context</description></item>
+/// </list>
+/// </remarks>
+public sealed class CausalSpanAttributionProcessor : BaseProcessor<Activity>
+{
+    private readonly ILogger<CausalSpanAttributionProcessor> _logger;
+
+    /// <summary>Initializes a new instance of <see cref="CausalSpanAttributionProcessor"/>.</summary>
+    public CausalSpanAttributionProcessor(ILogger<CausalSpanAttributionProcessor> logger)
+    {
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public override void OnEnd(Activity data)
+    {
+        // Only process execute_tool spans
+        var operationName = data.GetTagItem(ToolConventions.GenAiOperationName) as string;
+        if (operationName != ToolConventions.ExecuteToolOperation)
+            return;
+
+        // Bridge agent.tool.name → gen_ai.tool.name (OTel GenAI semantic convention)
+        var toolName = data.GetTagItem(ToolConventions.Name) as string;
+        if (toolName is not null)
+            data.SetTag(ToolConventions.GenAiToolName, toolName);
+
+        // Input hash — only when full data is requested (performance guard)
+        if (data.IsAllDataRequested)
+        {
+            var inputValue = data.GetTagItem(ToolConventions.ToolCallResult) as string ?? string.Empty;
+            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(inputValue));
+            var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
+            data.SetTag(ToolConventions.InputHash, hashHex);
+        }
+
+        // Result category from span status or existing tag
+        var existingCategory = data.GetTagItem(ToolConventions.ResultCategory) as string;
+        if (existingCategory is null)
+        {
+            var category = data.Status switch
+            {
+                ActivityStatusCode.Ok => TraceResultCategories.Success,
+                ActivityStatusCode.Error => TraceResultCategories.Error,
+                _ => TraceResultCategories.Success // default to success for unset status
+            };
+            data.SetTag(ToolConventions.ResultCategory, category);
+        }
+
+        // Candidate ID from baggage — only present in optimization eval contexts
+        var candidateId = data.GetBaggageItem(ToolConventions.HarnessCandidateId);
+        if (candidateId is not null)
+            data.SetTag(ToolConventions.HarnessCandidateId, candidateId);
+
+        var iteration = data.GetBaggageItem(ToolConventions.HarnessIteration);
+        if (iteration is not null)
+            data.SetTag(ToolConventions.HarnessIteration, iteration);
+
+        _logger.LogTrace(
+            "CausalSpanAttributionProcessor enriched span {SpanId} for tool {ToolName}",
+            data.SpanId, toolName);
+    }
+}
diff --git a/src/Content/Tests/Application.AI.Common.Tests/Factories/AgentExecutionContextFactoryTests.cs b/src/Content/Tests/Application.AI.Common.Tests/Factories/AgentExecutionContextFactoryTests.cs
index aa0ce9d..5f97005 100644
--- a/src/Content/Tests/Application.AI.Common.Tests/Factories/AgentExecutionContextFactoryTests.cs
+++ b/src/Content/Tests/Application.AI.Common.Tests/Factories/AgentExecutionContextFactoryTests.cs
@@ -2,9 +2,11 @@ using Application.AI.Common.Factories;
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.Context;
 using Application.AI.Common.Interfaces.Tools;
+using Application.AI.Common.Interfaces.Traces;
 using Domain.AI.Skills;
 using Domain.Common.Config;
 using Domain.Common.Config.AI;
+using Domain.Common.MetaHarness;
 using FluentAssertions;
 using Microsoft.Extensions.DependencyInjection;
 using Microsoft.Extensions.Logging.Abstractions;
@@ -18,7 +20,8 @@ public class AgentExecutionContextFactoryTests
 {
     private static AgentExecutionContextFactory CreateFactory(
         AIAgentFrameworkClientType configuredClientType = AIAgentFrameworkClientType.AzureOpenAI,
-        string? deployment = "default-model")
+        string? deployment = "default-model",
+        IExecutionTraceStore? traceStore = null)
     {
         var appConfig = new AppConfig
         {
@@ -43,7 +46,8 @@ public class AgentExecutionContextFactoryTests
             NullLoggerFactory.Instance,
             toolConverter: null,
             mcpToolProvider: null,
-            budgetTracker: null);
+            budgetTracker: null,
+            traceStore: traceStore);
     }
 
     private static SkillDefinition SimpleSkill(string id = "test-skill") => new()
@@ -104,4 +108,82 @@ public class AgentExecutionContextFactoryTests
         // Assert — falls back to AzureOpenAI as last resort
         context.AIAgentFrameworkType.Should().Be(AIAgentFrameworkClientType.AzureOpenAI);
     }
+
+    // --- Regression: trace scope wiring ---
+
+    [Fact]
+    public async Task CreateContext_WithoutTraceScope_SetsForExecutionScopeOnContext()
+    {
+        // Arrange — no TraceScope in options → factory creates ForExecution scope
+        var factory = CreateFactory();
+        var options = new SkillAgentOptions(); // TraceScope is null
+
+        // Act
+        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);
+
+        // Assert — context has a non-empty ForExecution scope (no optimization, no candidate)
+        context.TraceScope.Should().NotBeNull();
+        context.TraceScope!.ExecutionRunId.Should().NotBe(Guid.Empty);
+        context.TraceScope.OptimizationRunId.Should().BeNull();
+        context.TraceScope.CandidateId.Should().BeNull();
+    }
+
+    [Fact]
+    public async Task CreateContext_WithTraceStoreAndNoScope_CallsStartRunAsync()
+    {
+        // Arrange — factory has traceStore; options has no TraceScope
+        var mockWriter = Mock.Of<ITraceWriter>();
+        var traceStore = new Mock<IExecutionTraceStore>();
+        traceStore
+            .Setup(s => s.StartRunAsync(
+                It.IsAny<TraceScope>(),
+                It.IsAny<RunMetadata>(),
+                It.IsAny<CancellationToken>()))
+            .ReturnsAsync(mockWriter);
+
+        var factory = CreateFactory(traceStore: traceStore.Object);
+
+        // Act
+        await factory.MapToAgentContextAsync(SimpleSkill(), new SkillAgentOptions());
+
+        // Assert — StartRunAsync was called with a ForExecution scope
+        traceStore.Verify(s => s.StartRunAsync(
+            It.Is<TraceScope>(ts => ts.OptimizationRunId == null && ts.CandidateId == null),
+            It.IsAny<RunMetadata>(),
+            It.IsAny<CancellationToken>()), Times.Once);
+    }
+
+    [Fact]
+    public async Task CreateContext_WithExplicitTraceScope_UsesProvidedScope()
+    {
+        // Arrange — options supplies a pre-built scope (eval scenario)
+        var mockWriter = Mock.Of<ITraceWriter>();
+        var traceStore = new Mock<IExecutionTraceStore>();
+        traceStore
+            .Setup(s => s.StartRunAsync(
+                It.IsAny<TraceScope>(),
+                It.IsAny<RunMetadata>(),
+                It.IsAny<CancellationToken>()))
+            .ReturnsAsync(mockWriter);
+
+        var factory = CreateFactory(traceStore: traceStore.Object);
+        var expectedScope = new TraceScope
+        {
+            ExecutionRunId = Guid.NewGuid(),
+            OptimizationRunId = Guid.NewGuid(),
+            CandidateId = Guid.NewGuid()
+        };
+        var options = new SkillAgentOptions { TraceScope = expectedScope };
+
+        // Act
+        await factory.MapToAgentContextAsync(SimpleSkill(), options);
+
+        // Assert — the provided scope (not a new one) is passed to StartRunAsync
+        traceStore.Verify(s => s.StartRunAsync(
+            It.Is<TraceScope>(ts =>
+                ts.ExecutionRunId == expectedScope.ExecutionRunId &&
+                ts.CandidateId == expectedScope.CandidateId),
+            It.IsAny<RunMetadata>(),
+            It.IsAny<CancellationToken>()), Times.Once);
+    }
 }
diff --git a/src/Content/Tests/Application.AI.Common.Tests/Middleware/ToolDiagnosticsMiddlewareTests.cs b/src/Content/Tests/Application.AI.Common.Tests/Middleware/ToolDiagnosticsMiddlewareTests.cs
new file mode 100644
index 0000000..46ba536
--- /dev/null
+++ b/src/Content/Tests/Application.AI.Common.Tests/Middleware/ToolDiagnosticsMiddlewareTests.cs
@@ -0,0 +1,127 @@
+using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.Traces;
+using Application.AI.Common.Middleware;
+using Domain.Common.MetaHarness;
+using FluentAssertions;
+using Microsoft.Extensions.AI;
+using Microsoft.Extensions.Logging.Abstractions;
+using Moq;
+using Xunit;
+
+namespace Application.AI.Common.Tests.Middleware;
+
+public sealed class ToolDiagnosticsMiddlewareTests
+{
+    private static Mock<IChatClient> MakeChatClient(ChatResponse? response = null)
+    {
+        var mock = new Mock<IChatClient>();
+        mock.Setup(c => c.GetResponseAsync(
+                It.IsAny<IEnumerable<ChatMessage>>(),
+                It.IsAny<ChatOptions?>(),
+                It.IsAny<CancellationToken>()))
+            .ReturnsAsync(response ?? new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
+        mock.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);
+        return mock;
+    }
+
+    private static (Mock<ITraceWriter> Writer, ToolDiagnosticsMiddleware Middleware)
+        MakeMiddlewareWithWriter(Mock<IChatClient> innerClient)
+    {
+        var scope = TraceScope.ForExecution(Guid.NewGuid());
+        var writerMock = new Mock<ITraceWriter>();
+        writerMock.Setup(w => w.Scope).Returns(scope);
+        writerMock
+            .Setup(w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()))
+            .Returns(Task.CompletedTask);
+
+        var middleware = new ToolDiagnosticsMiddleware(
+            innerClient.Object,
+            NullLogger<ToolDiagnosticsMiddleware>.Instance,
+            traceWriter: writerMock.Object);
+
+        return (writerMock, middleware);
+    }
+
+    // --- Regression: trace appending when function results present ---
+
+    [Fact]
+    public async Task InvokeNext_WhenFunctionResultsInMessages_AppendsTraceRecord()
+    {
+        var innerClient = MakeChatClient();
+        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);
+
+        var messages = new ChatMessage[]
+        {
+            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "file content")])
+        };
+
+        await middleware.GetResponseAsync(messages, null, CancellationToken.None);
+
+        writerMock.Verify(
+            w => w.AppendTraceAsync(
+                It.Is<ExecutionTraceRecord>(r => r.Type == TraceRecordTypes.ToolResult),
+                It.IsAny<CancellationToken>()),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task InvokeNext_AppendTraceThrows_DoesNotRethrow()
+    {
+        var innerClient = MakeChatClient();
+        var scope = TraceScope.ForExecution(Guid.NewGuid());
+        var writerMock = new Mock<ITraceWriter>();
+        writerMock.Setup(w => w.Scope).Returns(scope);
+        writerMock
+            .Setup(w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new IOException("disk full"));
+
+        var middleware = new ToolDiagnosticsMiddleware(
+            innerClient.Object,
+            NullLogger<ToolDiagnosticsMiddleware>.Instance,
+            traceWriter: writerMock.Object);
+
+        var messages = new ChatMessage[]
+        {
+            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "content")])
+        };
+
+        // Should not propagate the IOException from AppendTraceAsync
+        var act = () => middleware.GetResponseAsync(messages, null, CancellationToken.None);
+        await act.Should().NotThrowAsync();
+    }
+
+    [Fact]
+    public async Task InvokeNext_NoFunctionResultsInMessages_DoesNotCallAppendTrace()
+    {
+        var innerClient = MakeChatClient();
+        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);
+
+        var messages = new ChatMessage[]
+        {
+            new(ChatRole.User, "What is the weather?")
+        };
+
+        await middleware.GetResponseAsync(messages, null, CancellationToken.None);
+
+        writerMock.Verify(
+            w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()),
+            Times.Never);
+    }
+
+    [Fact]
+    public async Task InvokeNext_WithoutTraceWriter_DoesNotThrow()
+    {
+        var innerClient = MakeChatClient();
+        var middleware = new ToolDiagnosticsMiddleware(
+            innerClient.Object,
+            NullLogger<ToolDiagnosticsMiddleware>.Instance);
+
+        var messages = new ChatMessage[]
+        {
+            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "content")])
+        };
+
+        var act = () => middleware.GetResponseAsync(messages, null, CancellationToken.None);
+        await act.Should().NotThrowAsync();
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Traces/FileSystemExecutionTraceStoreTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Traces/FileSystemExecutionTraceStoreTests.cs
new file mode 100644
index 0000000..3ff4fc2
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Traces/FileSystemExecutionTraceStoreTests.cs
@@ -0,0 +1,351 @@
+using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.Traces;
+using Domain.Common.Config;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using FluentAssertions;
+using Infrastructure.AI.Traces;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Moq;
+using System.Text.Json;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Traces;
+
+public sealed class FileSystemExecutionTraceStoreTests : IDisposable
+{
+    private readonly string _tempDir;
+    private readonly FileSystemExecutionTraceStore _sut;
+    private readonly Mock<ISecretRedactor> _redactor;
+
+    public FileSystemExecutionTraceStoreTests()
+    {
+        _tempDir = Path.Combine(Path.GetTempPath(), "trace-store-tests-" + Guid.NewGuid().ToString("N"));
+        Directory.CreateDirectory(_tempDir);
+
+        _redactor = new Mock<ISecretRedactor>();
+        _redactor.Setup(r => r.Redact(It.IsAny<string?>())).Returns<string?>(s => s); // passthrough
+
+        var config = new AppConfig
+        {
+            MetaHarness = new MetaHarnessConfig
+            {
+                TraceDirectoryRoot = _tempDir,
+                MaxFullPayloadKB = 1 // 1 KB for tests
+            }
+        };
+
+        _sut = new FileSystemExecutionTraceStore(
+            Options.Create(config),
+            _redactor.Object,
+            NullLogger<FileSystemExecutionTraceStore>.Instance);
+    }
+
+    public void Dispose()
+    {
+        try
+        {
+            if (Directory.Exists(_tempDir))
+                Directory.Delete(_tempDir, recursive: true);
+        }
+        catch
+        {
+            // Best-effort test cleanup
+        }
+    }
+
+    private static RunMetadata DefaultMetadata(string agentName = "test-agent") => new()
+    {
+        AgentName = agentName,
+        StartedAt = DateTimeOffset.UtcNow
+    };
+
+    // --- Directory creation ---
+
+    [Fact]
+    public async Task StartRunAsync_WhenNoOptimizationId_CreatesRunDirectoryUnderExecutions()
+    {
+        var scope = TraceScope.ForExecution(Guid.NewGuid());
+
+        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());
+
+        writer.RunDirectory.Should().StartWith(Path.Combine(_tempDir, "executions"));
+        Directory.Exists(writer.RunDirectory).Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task StartRunAsync_WhenOptimizationIdProvided_CreatesRunDirectoryUnderOptimizations()
+    {
+        var scope = new TraceScope
+        {
+            ExecutionRunId = Guid.NewGuid(),
+            OptimizationRunId = Guid.NewGuid()
+        };
+
+        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());
+
+        writer.RunDirectory.Should().Contain("optimizations");
+        Directory.Exists(writer.RunDirectory).Should().BeTrue();
+    }
+
+    // --- Manifest ---
+
+    [Fact]
+    public async Task StartRunAsync_WritesManifestJson_WithWriteCompletedFalse()
+    {
+        var scope = TraceScope.ForExecution(Guid.NewGuid());
+
+        var writer = await _sut.StartRunAsync(scope, DefaultMetadata("my-agent"));
+
+        var manifestPath = Path.Combine(writer.RunDirectory, "manifest.json");
+        File.Exists(manifestPath).Should().BeTrue();
+
+        var json = await File.ReadAllTextAsync(manifestPath);
+        using var doc = JsonDocument.Parse(json);
+        doc.RootElement.GetProperty("write_completed").GetBoolean().Should().BeFalse();
+        doc.RootElement.GetProperty("agent_name").GetString().Should().Be("my-agent");
+    }
+
+    [Fact]
+    public async Task StartRunAsync_ManifestJson_ContainsExecutionRunId()
+    {
+        var runId = Guid.NewGuid();
+        var scope = TraceScope.ForExecution(runId);
+
+        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());
+
+        var json = await File.ReadAllTextAsync(Path.Combine(writer.RunDirectory, "manifest.json"));
+        json.Should().Contain(runId.ToString("D"));
+    }
+
+    // --- Turn artifacts ---
+
+    [Fact]
+    public async Task WriteTurnAsync_CreatesExpectedSubdirectoryWithArtifactFiles()
+    {
+        var writer = await _sut.StartRunAsync(TraceScope.ForExecution(Guid.NewGuid()), DefaultMetadata());
+        var artifacts = new TurnArtifacts
+        {
+            TurnNumber = 1,
+            SystemPrompt = "You are a test agent.",
+            ModelResponse = "Hello, I am ready.",
+            ToolCallsJsonl = "{\"name\":\"file_read\"}",
+            StateSnapshot = "{\"step\":1}"
+        };
+
+        await writer.WriteTurnAsync(1, artifacts);
+
+        var turnDir = Path.Combine(writer.RunDirectory, "turns", "1");
+        Directory.Exists(turnDir).Should().BeTrue();
+        File.Exists(Path.Combine(turnDir, "system_prompt.md")).Should().BeTrue();
+        File.Exists(Path.Combine(turnDir, "model_response.md")).Should().BeTrue();
+        File.Exists(Path.Combine(turnDir, "tool_calls.jsonl")).Should().BeTrue();
+        File.Exists(Path.Combine(turnDir, "state_snapshot.json")).Should().BeTrue();
+    }
+
+    [Fact]
+    public async Task WriteTurnAsync_AppliesSecretRedactor_ToSystemPrompt()
+    {
+        _redactor.Setup(r => r.Redact("secret-prompt")).Returns("[REDACTED]");
+
+        var writer = await _sut.StartRunAsync(TraceScope.ForExecution(Guid.NewGuid()), DefaultMetadata());
+        var artifacts = new TurnArtifacts { TurnNumber = 1, SystemPrompt = "secret-prompt" };
+
+        await writer.WriteTurnAsync(1, artifacts);
+
+        var content = await File.ReadAllTextAsync(
+            Path.Combine(writer.RunDirectory, "turns", "1", "system_prompt.md"));
+        content.Should().Be("[REDACTED]");
+    }
+
+    // --- JSONL trace appending ---
+
+    [Fact]
+    public async Task AppendTraceAsync_WritesValidJsonlLine_ToTracesFile()
+    {
+        var scope = TraceScope.ForExecution(Guid.NewGuid());
+        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());
+        var record = new ExecutionTraceRecord
+        {
+            Type = TraceRecordTypes.Observation,
+            ExecutionRunId = scope.ExecutionRunId.ToString("D"),
+            TurnId = Guid.NewGuid().ToString("D"),
+            PayloadSummary = "test payload"
+        };
+
+        await writer.AppendTraceAsync(record);
+
+        var tracesPath = Path.Combine(writer.RunDirectory, "traces.jsonl");
+        File.Exists(tracesPath).Should().BeTrue();
+        var lines = await File.ReadAllLinesAsync(tracesPath);
+        lines.Should().HaveCount(1);
+
+        using var doc = JsonDocument.Parse(lines[0]);
+        doc.RootElement.GetProperty("type").GetString().Should().Be(TraceRecordTypes.Observation);
+    }
+
+    [Fact]
+    public async Task AppendTraceAsync_AssignsMonotonicallyIncreasingSeqNumbers()
+    {
+        var scope = TraceScope.ForExecution(Guid.NewGuid());
+        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());
+
+        for (var i = 0; i < 5; i++)
+        {
+            await writer.AppendTraceAsync(new ExecutionTraceRecord
+            {
+                Type = TraceRecordTypes.Observation,
+                ExecutionRunId = scope.ExecutionRunId.ToString("D"),
+                TurnId = "turn-1"
+            });
+        }
+
+        var lines = await File.ReadAllLinesAsync(Path.Combine(writer.RunDirectory, "traces.jsonl"));
+        var seqNumbers = lines
+            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("seq").GetInt64())
+            .ToList();
+
+        seqNumbers.Should().BeInAscendingOrder();
+        seqNumbers.Distinct().Should().HaveCount(5, "sequence numbers must be unique");
+    }
+
+    [Fact]
+    public async Task AppendTraceAsync_ConcurrentWrites_DoNotCorruptJsonl()
+    {
+        var scope = TraceScope.ForExecution(Guid.NewGuid());
+        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());
+
+        var tasks = Enumerable.Range(0, 10).Select(taskIdx =>
+            Task.Run(async () =>
+            {
+                for (var j = 0; j < 20; j++)
+                {
+                    await writer.AppendTraceAsync(new ExecutionTraceRecord
+                    {
+                        Type = TraceRecordTypes.Observation,
+                        ExecutionRunId = scope.ExecutionRunId.ToString("D"),
+                        TurnId = $"task-{taskIdx}-turn-{j}"
+                    });
+                }
+            }));
+
+        await Task.WhenAll(tasks);
+
+        var tracesPath = Path.Combine(writer.RunDirectory, "traces.jsonl");
+        var lines = await File.ReadAllLinesAsync(tracesPath);
+        lines.Should().HaveCount(200, "all 10*20 writes must be present");
+
+        // Every line must be valid JSON and seq numbers must be unique
+        var seqNumbers = new List<long>(200);
+        foreach (var line in lines)
+        {
+            var act = () => JsonDocument.Parse(line);
+            act.Should().NotThrow("every line must be valid JSON");
+            var seq = JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt64();
+            seqNumbers.Add(seq);
+        }
+
+        seqNumbers.Distinct().Should().HaveCount(200, "no duplicate sequence numbers");
+    }
+
+    // --- Redaction in trace records ---
+
+    [Fact]
+    public async Task AppendTraceAsync_AppliesRedaction_WhenPayloadContainsSecret()
+    {
+        _redactor.Setup(r => r.Redact("sk-secret-key")).Returns("[REDACTED]");
+
+        var scope = TraceScope.ForExecution(Guid.NewGuid());
+        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());
+
+        await writer.AppendTraceAsync(new ExecutionTraceRecord
+        {
+            Type = TraceRecordTypes.ToolResult,
+            ExecutionRunId = scope.ExecutionRunId.ToString("D"),
+            TurnId = "t1",
+            PayloadSummary = "sk-secret-key"
+        });
+
+        var line = (await File.ReadAllLinesAsync(Path.Combine(writer.RunDirectory, "traces.jsonl")))[0];
+        line.Should().Contain("[REDACTED]");
+        line.Should().NotContain("sk-secret-key");
+    }
+
+    // --- Large payload splitting ---
+
+    [Fact]
+    public async Task WriteTurnAsync_LargeToolResult_WritesToToolResultsDirectory()
+    {
+        var writer = await _sut.StartRunAsync(TraceScope.ForExecution(Guid.NewGuid()), DefaultMetadata());
+        var largePayload = new string('x', 2 * 1024); // 2 KB > MaxFullPayloadKB of 1 KB
+
+        var artifacts = new TurnArtifacts
+        {
+            TurnNumber = 1,
+            ToolResults = new Dictionary<string, string>
+            {
+                ["call-abc"] = largePayload
+            }
+        };
+
+        await writer.WriteTurnAsync(1, artifacts);
+
+        var toolResultFile = Path.Combine(writer.RunDirectory, "turns", "1", "tool_results", "call-abc.json");
+        File.Exists(toolResultFile).Should().BeTrue();
+        var content = await File.ReadAllTextAsync(toolResultFile);
+        content.Should().Be(largePayload);
+    }
+
+    // --- Atomic writes ---
+
+    [Fact]
+    public async Task WriteScoresAsync_WritesScoresJson_WithValidContent()
+    {
+        var writer = await _sut.StartRunAsync(TraceScope.ForExecution(Guid.NewGuid()), DefaultMetadata());
+        var scores = new HarnessScores
+        {
+            PassRate = 0.85,
+            TotalTokenCost = 1500,
+            ScoredAt = DateTimeOffset.UtcNow
+        };
+
+        await writer.WriteScoresAsync(scores);
+
+        var scoresPath = Path.Combine(writer.RunDirectory, "scores.json");
+        File.Exists(scoresPath).Should().BeTrue();
+        var json = await File.ReadAllTextAsync(scoresPath);
+        using var doc = JsonDocument.Parse(json);
+        doc.RootElement.GetProperty("pass_rate").GetDouble().Should().BeApproximately(0.85, 0.001);
+    }
+
+    // --- CompleteAsync ---
+
+    [Fact]
+    public async Task CompleteAsync_SetsWriteCompletedTrue_InManifest()
+    {
+        var writer = await _sut.StartRunAsync(TraceScope.ForExecution(Guid.NewGuid()), DefaultMetadata());
+
+        await writer.CompleteAsync();
+
+        var json = await File.ReadAllTextAsync(Path.Combine(writer.RunDirectory, "manifest.json"));
+        using var doc = JsonDocument.Parse(json);
+        doc.RootElement.GetProperty("write_completed").GetBoolean().Should().BeTrue();
+    }
+
+    // --- GetRunDirectoryAsync ---
+
+    [Fact]
+    public async Task GetRunDirectoryAsync_ReturnsCorrectAbsolutePath()
+    {
+        var runId = Guid.NewGuid();
+        var scope = TraceScope.ForExecution(runId);
+
+        var dir = await _sut.GetRunDirectoryAsync(scope);
+
+        dir.Should().StartWith(_tempDir);
+        dir.Should().Contain(runId.ToString("D").ToLowerInvariant());
+        // GetRunDirectoryAsync does NOT create the directory
+        Directory.Exists(dir).Should().BeFalse();
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.Observability.Tests/Processors/CausalSpanAttributionProcessorTests.cs b/src/Content/Tests/Infrastructure.Observability.Tests/Processors/CausalSpanAttributionProcessorTests.cs
new file mode 100644
index 0000000..33a7f34
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.Observability.Tests/Processors/CausalSpanAttributionProcessorTests.cs
@@ -0,0 +1,167 @@
+using System.Diagnostics;
+using Domain.AI.Telemetry.Conventions;
+using Domain.Common.MetaHarness;
+using FluentAssertions;
+using Infrastructure.Observability.Processors;
+using Microsoft.Extensions.Logging.Abstractions;
+using Xunit;
+
+namespace Infrastructure.Observability.Tests.Processors;
+
+public sealed class CausalSpanAttributionProcessorTests : IDisposable
+{
+    private readonly ActivitySource _source = new("test.causal-attribution");
+    private readonly ActivityListener _listener;
+
+    public CausalSpanAttributionProcessorTests()
+    {
+        _listener = new ActivityListener
+        {
+            ShouldListenTo = _ => true,
+            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
+                ActivitySamplingResult.AllDataAndRecorded
+        };
+        ActivitySource.AddActivityListener(_listener);
+    }
+
+    public void Dispose()
+    {
+        _listener.Dispose();
+        _source.Dispose();
+    }
+
+    private static CausalSpanAttributionProcessor CreateProcessor() =>
+        new(NullLogger<CausalSpanAttributionProcessor>.Instance);
+
+    [Fact]
+    public void OnEnd_ToolCallSpan_AddsToolNameTag()
+    {
+        var processor = CreateProcessor();
+        using var activity = _source.StartActivity("execute-tool")!;
+        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
+        activity.SetTag(ToolConventions.Name, "file_read");
+
+        processor.OnEnd(activity);
+
+        activity.GetTagItem(ToolConventions.GenAiToolName).Should().Be("file_read");
+    }
+
+    [Fact]
+    public void OnEnd_ToolCallSpan_AddsInputHashTag()
+    {
+        var processor = CreateProcessor();
+        using var activity = _source.StartActivity("execute-tool")!;
+        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
+        activity.SetTag(ToolConventions.Name, "file_read");
+        activity.SetTag(ToolConventions.ToolCallResult, "some result content");
+
+        processor.OnEnd(activity);
+
+        var hash = activity.GetTagItem(ToolConventions.InputHash) as string;
+        hash.Should().NotBeNullOrEmpty();
+        hash.Should().MatchRegex("^[0-9a-f]{64}$", "input hash must be a lowercase 64-char SHA-256 hex string");
+    }
+
+    [Fact]
+    public void OnEnd_ToolCallSpan_AddsResultCategoryTag_Success()
+    {
+        var processor = CreateProcessor();
+        using var activity = _source.StartActivity("execute-tool")!;
+        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
+        activity.SetStatus(ActivityStatusCode.Ok);
+
+        processor.OnEnd(activity);
+
+        activity.GetTagItem(ToolConventions.ResultCategory).Should().Be(TraceResultCategories.Success);
+    }
+
+    [Fact]
+    public void OnEnd_ToolCallSpan_AddsResultCategoryTag_Error()
+    {
+        var processor = CreateProcessor();
+        using var activity = _source.StartActivity("execute-tool")!;
+        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
+        activity.SetStatus(ActivityStatusCode.Error);
+
+        processor.OnEnd(activity);
+
+        activity.GetTagItem(ToolConventions.ResultCategory).Should().Be(TraceResultCategories.Error);
+    }
+
+    [Fact]
+    public void OnEnd_WhenCandidateIdOnContext_AddsCandidateIdTag()
+    {
+        var processor = CreateProcessor();
+        using var activity = _source.StartActivity("execute-tool")!;
+        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
+        activity.AddBaggage(ToolConventions.HarnessCandidateId, "cand-abc-123");
+
+        processor.OnEnd(activity);
+
+        activity.GetTagItem(ToolConventions.HarnessCandidateId).Should().Be("cand-abc-123");
+    }
+
+    [Fact]
+    public void OnEnd_WhenNoCandidateId_DoesNotAddCandidateIdTag()
+    {
+        var processor = CreateProcessor();
+        using var activity = _source.StartActivity("execute-tool")!;
+        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
+        // No candidate baggage
+
+        processor.OnEnd(activity);
+
+        activity.GetTagItem(ToolConventions.HarnessCandidateId).Should().BeNull();
+    }
+
+    [Fact]
+    public void OnEnd_InputHashComputation_IsNotPerformedWhenIsAllDataRequestedFalse()
+    {
+        // A listener returning PropagationData means IsAllDataRequested = false;
+        // tags set on the activity are no-ops, so gen_ai.operation.name won't be stored.
+        // The processor must NOT compute hashes when IsAllDataRequested = false.
+        using var lowDataSource = new ActivitySource("test.causal-attribution.lowdata");
+        using var lowDataListener = new ActivityListener
+        {
+            ShouldListenTo = _ => true,
+            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
+                ActivitySamplingResult.PropagationData
+        };
+        ActivitySource.AddActivityListener(lowDataListener);
+
+        var processor = CreateProcessor();
+        using var activity = lowDataSource.StartActivity("execute-tool")!;
+
+        processor.OnEnd(activity);
+
+        // IsAllDataRequested = false → no hash computed → tag absent
+        activity.GetTagItem(ToolConventions.InputHash).Should().BeNull();
+    }
+
+    [Fact]
+    public void OnEnd_NonToolSpan_DoesNotAddCausalAttributes()
+    {
+        var processor = CreateProcessor();
+        using var activity = _source.StartActivity("chat-completion")!;
+        activity.SetTag(ToolConventions.GenAiOperationName, "chat");
+
+        processor.OnEnd(activity);
+
+        activity.GetTagItem(ToolConventions.GenAiToolName).Should().BeNull();
+        activity.GetTagItem(ToolConventions.InputHash).Should().BeNull();
+        activity.GetTagItem(ToolConventions.ResultCategory).Should().BeNull();
+    }
+
+    [Fact]
+    public void OnEnd_SpanWithNoOperationName_DoesNotAddCausalAttributes()
+    {
+        var processor = CreateProcessor();
+        using var activity = _source.StartActivity("plain-span")!;
+        // No gen_ai.operation.name tag at all
+
+        processor.OnEnd(activity);
+
+        activity.GetTagItem(ToolConventions.GenAiToolName).Should().BeNull();
+        activity.GetTagItem(ToolConventions.ResultCategory).Should().BeNull();
+    }
+}
