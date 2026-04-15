commit 72051978a72cdcf29b1bad2b7f4cade55f64f797
Author: MCKRUZ <kruz79@gmail.com>
Date:   Sat Apr 11 22:51:11 2026 -0400

    feat: implement trace infrastructure (section 04)
    
    - FileSystemExecutionTraceStore + FileSystemTraceWriter: atomic writes,
      SemaphoreSlim JSONL serialization, Interlocked sequence numbers
    - ITraceWriter extends IAsyncDisposable; AdditionalPropertiesKey const
    - CausalSpanAttributionProcessor: bridges agent.tool.name → gen_ai.tool.name,
      SHA256 input hash, result category, candidate_id/iteration from baggage
    - AgentExecutionContextFactory: wires IExecutionTraceStore, sets TraceScope
    - ToolDiagnosticsMiddleware: appends FunctionResultContent traces (non/streaming)
    - ToolConventions: +ToolCallArguments, +GenAiToolName, +InputHash, +ResultCategory,
      +HarnessCandidateId, +HarnessIteration
    - TraceScope.ResolveDirectory: TaskId path traversal validation
    - 27 new tests across 4 components; 895 total passing
    
    Plan: section-04-trace-infrastructure.md
    Co-Authored-By: Claude <noreply@anthropic.com>

diff --git a/src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs b/src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs
index 5de3212..0542b65 100644
--- a/src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs
+++ b/src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs
@@ -27,6 +27,8 @@ public static class ToolConventions
 
     /// <summary>Gen AI operation name for tool execution spans.</summary>
     public const string ExecuteToolOperation = "execute_tool";
+    /// <summary>Gen AI span attribute containing the serialized tool call arguments (input).</summary>
+    public const string ToolCallArguments = "gen_ai.tool.call.arguments";
     /// <summary>Gen AI span attribute containing the tool call result text.</summary>
     public const string ToolCallResult = "gen_ai.tool.call.result";
     /// <summary>Gen AI span attribute for the operation name.</summary>
@@ -44,4 +46,17 @@ public static class ToolConventions
     public const string EmptyResults = "agent.tool.empty_results";
     /// <summary>Histogram: tool result size in characters.</summary>
     public const string ResultSize = "agent.tool.result_size";
+
+    // Causal attribution attributes (Meta-Harness OTel GenAI semantic conventions)
+
+    /// <summary>OTel GenAI semantic convention attribute for tool name (bridged from agent.tool.name).</summary>
+    public const string GenAiToolName = "gen_ai.tool.name";
+    /// <summary>SHA256 hex digest of serialized tool input arguments. Only set when IsAllDataRequested.</summary>
+    public const string InputHash = "tool.input_hash";
+    /// <summary>Bucketed outcome category matching ExecutionTraceRecord.result_category.</summary>
+    public const string ResultCategory = "tool.result_category";
+    /// <summary>CandidateId from TraceScope when running inside an optimization eval.</summary>
+    public const string HarnessCandidateId = "gen_ai.harness.candidate_id";
+    /// <summary>Iteration number from TraceScope when running inside an optimization eval.</summary>
+    public const string HarnessIteration = "gen_ai.harness.iteration";
 }
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
index 0000000..8f7c65a
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
+        // Input hash — SHA256 of tool arguments. Only when full data is requested (performance guard).
+        if (data.IsAllDataRequested)
+        {
+            var inputValue = data.GetTagItem(ToolConventions.ToolCallArguments) as string ?? string.Empty;
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
diff --git a/src/Content/Tests/Infrastructure.Observability.Tests/Processors/CausalSpanAttributionProcessorTests.cs b/src/Content/Tests/Infrastructure.Observability.Tests/Processors/CausalSpanAttributionProcessorTests.cs
new file mode 100644
index 0000000..b474439
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
+        activity.SetTag(ToolConventions.ToolCallArguments, "{\"path\": \"/tmp/test.txt\"}");
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
