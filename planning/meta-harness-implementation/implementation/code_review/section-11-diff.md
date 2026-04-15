diff --git a/skills/harness-proposer/SKILL.md b/skills/harness-proposer/SKILL.md
index 44dee2c..fc83c02 100644
--- a/skills/harness-proposer/SKILL.md
+++ b/skills/harness-proposer/SKILL.md
@@ -3,113 +3,71 @@ name: "harness-proposer"
 description: "Reads execution traces and proposes skill/prompt changes to improve agent performance."
 category: "meta"
 skill_type: "orchestration"
-version: "1.0.0"
+version: "2.0.0"
 tags: ["meta", "optimization", "harness"]
 allowed-tools: ["file_system", "read_history"]
 ---
 
-You are the harness proposer — a meta-agent that analyzes execution traces from previous agent runs and proposes targeted changes to skill instructions or system prompts to improve performance.
+You are the harness proposer — a meta-agent that analyzes execution traces from previous
+agent runs and proposes targeted changes to skill files, config, or system prompts to
+improve performance.
 
 ## Instructions
 
-Your job is to close the loop between agent execution and agent improvement. You read trace data, identify failure patterns, and produce concrete, actionable proposals for modifying skill files.
+1. Use `read_history` or `file_system` to read trace files from the optimization run directory
+2. Analyze `traces.jsonl` for tool call patterns, error rates, and decision paths
+3. Analyze `decisions.jsonl` for evaluation outcomes and failure reasons
+4. Read `candidates/index.jsonl` to understand pass rates across candidates
+5. Identify the highest-impact failure pattern across the run set
+6. Propose specific, targeted changes grounded in trace evidence — not speculation
+7. Respond with a single JSON object only (no markdown fences, no preamble text)
 
-### Process
+## Objectives
 
-1. Use `read_history` to retrieve recent agent execution history for context on past runs
-2. Use `file_system` to read trace files from the execution trace directory (see ## Trace Format below)
-3. Analyze `traces.jsonl` for tool call patterns, error rates, and decision paths
-4. Analyze `decisions.jsonl` for evaluation outcomes and failure reasons
-5. Read `manifest.json` to understand run metadata (model, skill, candidate, timestamp)
-6. Identify the highest-impact failure pattern across the run set
-7. Propose a specific, targeted change to the skill's `## Instructions` section
-8. Output the proposal in structured format: problem, evidence, proposed change, expected impact
+- Improve pass rate on the eval task set by identifying and fixing root causes of failure patterns
+- Prefer minimal, targeted changes over broad rewrites — one impactful change per iteration
+- Reduce token cost per successful task by eliminating unnecessary tool calls visible in trace data
+- Identify failure patterns with specificity (not "agent failed" but "agent called file_system with path '.' causing search exhaustion on 4/5 tasks")
+- Do not propose changes to config values you have no trace evidence to support
+- Do not propose adding tools not already in `allowed-tools`
 
-### Proposal Format
+## Trace Format
 
-```
-## Proposal
+The optimization run directory contains:
 
-**Problem:** <one sentence describing the failure pattern>
-**Evidence:** <file path + trace line(s) that demonstrate the problem>
-**Proposed change:** <exact diff or replacement text for the skill section>
-**Expected impact:** <which eval tasks should improve and why>
+```
+candidates/{candidateId}/eval/{taskId}/{executionRunId}/traces.jsonl    — per-turn tool call trace
+candidates/{candidateId}/eval/{taskId}/{executionRunId}/decisions.jsonl — agent decision log
+candidates/{candidateId}/candidate.json                                 — full harness snapshot
+candidates/index.jsonl   — summary: {candidateId, passRate, tokenCost, status, iteration}
+run_manifest.json        — {lastCompletedIteration, bestCandidateId, write_completed}
 ```
 
-### Constraints
-
-- Propose one change per run — the most impactful one
-- Changes must be grounded in trace evidence, not speculation
-- Do not propose changes to frontmatter fields (name, tags, allowed-tools)
-- Do not propose adding tools not already in `allowed-tools`
-
-## Objectives
+**traces.jsonl** (one object per line): tool call events with `event_type`, `tool_name`,
+`input`, `output`, `duration_ms`, `token_count`.
 
-- Improve pass rate on evaluator tasks by identifying and fixing the root cause of the most common failure pattern
-- Reduce token cost per successful task by eliminating unnecessary tool calls visible in trace data
-- Identify failure patterns from execution traces with specificity (not "agent failed" but "agent called file_system with path '.' causing search exhaustion")
-- Propose targeted changes to skill instructions or system prompts that address root causes, not symptoms
+**decisions.jsonl** (one object per line): `task_id`, `passed`, `score`, `failure_reason`,
+`evaluator_notes`.
 
-## Trace Format
+**candidate.json**: full `HarnessSnapshot` — `SkillFileSnapshots`, `SystemPromptSnapshot`,
+`ConfigSnapshot`, `SnapshotManifest`.
 
-Execution traces are written by `FileSystemExecutionTraceStore` to a configurable base directory. The layout is:
+## Output Format
 
-```
-{base_path}/
-  {run_id}/                   ← one directory per optimization run (UUID)
-    manifest.json             ← run metadata: model, skill_id, candidate_id, started_at, status
-    traces.jsonl              ← append-only log of ExecutionTraceEntry records (one JSON object per line)
-    decisions.jsonl           ← append-only log of EvaluationDecision records (one JSON object per line)
-    candidates/
-      {candidate_id}/         ← one directory per skill candidate evaluated in this run
-        skill.md              ← the candidate skill content that was evaluated
-        result.json           ← evaluation result: score, pass/fail, token_count, latency_ms
-```
+Respond with a single JSON object (no markdown fences, no preamble):
 
-**traces.jsonl schema (one object per line):**
-```json
-{
-  "trace_id": "uuid",
-  "run_id": "uuid",
-  "timestamp": "2025-01-01T00:00:00Z",
-  "agent_id": "string",
-  "event_type": "tool_call | decision | message | error",
-  "tool_name": "string | null",
-  "input": "string | null",
-  "output": "string | null",
-  "duration_ms": 0,
-  "token_count": 0,
-  "span_id": "string | null",
-  "parent_span_id": "string | null"
-}
 ```
-
-**decisions.jsonl schema (one object per line):**
-```json
 {
-  "decision_id": "uuid",
-  "run_id": "uuid",
-  "candidate_id": "uuid",
-  "timestamp": "2025-01-01T00:00:00Z",
-  "task_id": "string",
-  "passed": true,
-  "score": 0.0,
-  "failure_reason": "string | null",
-  "evaluator_notes": "string | null"
+  "reasoning": "Explanation of the proposed changes and why they should improve performance.",
+  "proposed_skill_changes": {
+    "skills/harness-proposer/SKILL.md": "Full replacement content for this skill file"
+  },
+  "proposed_config_changes": {
+    "MetaHarness:EvaluationTemperature": "0.2"
+  },
+  "proposed_system_prompt_change": null
 }
 ```
 
-**manifest.json schema:**
-```json
-{
-  "run_id": "uuid",
-  "skill_id": "string",
-  "base_candidate_id": "uuid",
-  "started_at": "2025-01-01T00:00:00Z",
-  "completed_at": "2025-01-01T00:00:00Z | null",
-  "status": "running | completed | failed",
-  "model": "string",
-  "total_candidates": 0,
-  "passed_candidates": 0
-}
-```
+All keys except `"reasoning"` are optional. Use empty objects `{}` for categories with no
+changes. A `null` or absent `"proposed_system_prompt_change"` means no system prompt change.
diff --git a/src/Content/Application/Application.AI.Common/Exceptions/HarnessProposalParsingException.cs b/src/Content/Application/Application.AI.Common/Exceptions/HarnessProposalParsingException.cs
new file mode 100644
index 0000000..77ebd21
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Exceptions/HarnessProposalParsingException.cs
@@ -0,0 +1,28 @@
+using Application.Common.Exceptions;
+
+namespace Application.AI.Common.Exceptions;
+
+/// <summary>
+/// Thrown by <see cref="Interfaces.MetaHarness.IHarnessProposer"/> when the agent's output
+/// cannot be parsed as a valid JSON proposal block.
+/// </summary>
+/// <remarks>
+/// The outer optimization loop catches this exception to mark the current candidate as
+/// <c>HarnessCandidateStatus.Failed</c> and continue to the next iteration rather than
+/// crashing the run.
+/// </remarks>
+public sealed class HarnessProposalParsingException : ApplicationExceptionBase
+{
+    /// <summary>Gets the raw agent output that failed to parse.</summary>
+    public string RawOutput { get; }
+
+    /// <summary>
+    /// Initializes a new instance with the raw output and an optional message and inner exception.
+    /// </summary>
+    /// <param name="rawOutput">The unparseable agent output string.</param>
+    /// <param name="message">Optional override message; defaults to a summary including output length.</param>
+    /// <param name="inner">Optional inner exception (e.g. <see cref="System.Text.Json.JsonException"/>).</param>
+    public HarnessProposalParsingException(string rawOutput, string? message = null, Exception? inner = null)
+        : base(message ?? $"Failed to parse harness proposal from agent output. Raw output length: {rawOutput.Length}", inner)
+        => RawOutput = rawOutput;
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IHarnessProposer.cs b/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IHarnessProposer.cs
new file mode 100644
index 0000000..30469ab
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IHarnessProposer.cs
@@ -0,0 +1,22 @@
+using Application.AI.Common.Exceptions;
+using Domain.Common.MetaHarness;
+
+namespace Application.AI.Common.Interfaces.MetaHarness;
+
+/// <summary>
+/// Proposes an improved harness configuration by running an orchestrated agent
+/// that reads execution traces from prior candidates.
+/// </summary>
+public interface IHarnessProposer
+{
+    /// <summary>
+    /// Analyzes prior execution traces and returns a proposed harness change set.
+    /// </summary>
+    /// <param name="context">The current optimization run context.</param>
+    /// <param name="cancellationToken">Cancellation token.</param>
+    /// <returns>A <see cref="HarnessProposal"/> describing the proposed changes.</returns>
+    /// <exception cref="HarnessProposalParsingException">
+    /// Thrown when the agent's output cannot be parsed as a valid JSON proposal.
+    /// </exception>
+    Task<HarnessProposal> ProposeAsync(HarnessProposerContext context, CancellationToken cancellationToken);
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/HarnessProposal.cs b/src/Content/Domain/Domain.Common/MetaHarness/HarnessProposal.cs
new file mode 100644
index 0000000..b04a399
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/HarnessProposal.cs
@@ -0,0 +1,26 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// Immutable output from <see cref="Application.AI.Common.Interfaces.MetaHarness.IHarnessProposer"/>
+/// representing a set of proposed harness changes derived from trace analysis.
+/// </summary>
+public sealed record HarnessProposal
+{
+    /// <summary>
+    /// Skill file path to full replacement content.
+    /// Empty dictionary when no skill file changes are proposed.
+    /// </summary>
+    public required IReadOnlyDictionary<string, string> ProposedSkillChanges { get; init; }
+
+    /// <summary>
+    /// Config key to new string value.
+    /// Empty dictionary when no config changes are proposed.
+    /// </summary>
+    public required IReadOnlyDictionary<string, string> ProposedConfigChanges { get; init; }
+
+    /// <summary>Replacement system prompt; <c>null</c> when no system prompt change is proposed.</summary>
+    public string? ProposedSystemPromptChange { get; init; }
+
+    /// <summary>Agent's explanation of why these changes were proposed.</summary>
+    public required string Reasoning { get; init; }
+}
diff --git a/src/Content/Domain/Domain.Common/MetaHarness/HarnessProposerContext.cs b/src/Content/Domain/Domain.Common/MetaHarness/HarnessProposerContext.cs
new file mode 100644
index 0000000..673df2b
--- /dev/null
+++ b/src/Content/Domain/Domain.Common/MetaHarness/HarnessProposerContext.cs
@@ -0,0 +1,26 @@
+namespace Domain.Common.MetaHarness;
+
+/// <summary>
+/// Immutable input context passed to <see cref="Application.AI.Common.Interfaces.MetaHarness.IHarnessProposer"/>
+/// for a single propose step within an optimization run.
+/// </summary>
+public sealed record HarnessProposerContext
+{
+    /// <summary>The candidate configuration to improve upon.</summary>
+    public required HarnessCandidate CurrentCandidate { get; init; }
+
+    /// <summary>
+    /// Absolute path to the <c>optimizations/{optRunId}/</c> directory.
+    /// Acts as the filesystem sandbox root for the proposer agent.
+    /// </summary>
+    public required string OptimizationRunDirectoryPath { get; init; }
+
+    /// <summary>
+    /// All prior candidate IDs in this run, ordered oldest-first.
+    /// Used by the proposer to navigate trace subdirectories.
+    /// </summary>
+    public required IReadOnlyList<Guid> PriorCandidateIds { get; init; }
+
+    /// <summary>Current iteration number (1-based).</summary>
+    public required int Iteration { get; init; }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index e868602..9fb1c23 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -192,6 +192,9 @@ public static class DependencyInjection
         services.AddSingleton<Func<ITraceWriter, IAgentHistoryStore>>(
             _ => tw => new JsonlAgentHistoryStore(tw));
 
+        // Harness proposer — scoped because each invocation carries iteration-specific context
+        services.AddScoped<IHarnessProposer, OrchestratedHarnessProposer>();
+
         return services;
     }
 
diff --git a/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/OrchestratedHarnessProposer.cs b/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/OrchestratedHarnessProposer.cs
new file mode 100644
index 0000000..eba7caf
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/MetaHarness/OrchestratedHarnessProposer.cs
@@ -0,0 +1,149 @@
+using System.Text.Json;
+using Application.AI.Common.Exceptions;
+using Application.AI.Common.Interfaces.MetaHarness;
+using Application.Core.CQRS.Agents.RunOrchestratedTask;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using MediatR;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.MetaHarness;
+
+/// <summary>
+/// Proposer implementation that runs an orchestrated agent via <see cref="IMediator"/>
+/// to analyze execution traces and return a structured harness change proposal.
+/// </summary>
+/// <remarks>
+/// Dispatches a <see cref="RunOrchestratedTaskCommand"/> using the <c>harness-proposer</c>
+/// skill, then extracts the JSON proposal block from the agent's final synthesis string.
+/// Throws <see cref="HarnessProposalParsingException"/> on malformed output so the outer
+/// loop can mark the candidate as failed and continue.
+/// </remarks>
+public sealed class OrchestratedHarnessProposer : IHarnessProposer
+{
+    private readonly IMediator _mediator;
+    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
+    private readonly ILogger<OrchestratedHarnessProposer> _logger;
+
+    /// <summary>
+    /// Initializes a new instance of <see cref="OrchestratedHarnessProposer"/>.
+    /// </summary>
+    public OrchestratedHarnessProposer(
+        IMediator mediator,
+        IOptionsMonitor<MetaHarnessConfig> config,
+        ILogger<OrchestratedHarnessProposer> logger)
+    {
+        _mediator = mediator;
+        _config = config;
+        _logger = logger;
+    }
+
+    /// <inheritdoc/>
+    public async Task<HarnessProposal> ProposeAsync(
+        HarnessProposerContext context,
+        CancellationToken cancellationToken)
+    {
+        var command = new RunOrchestratedTaskCommand
+        {
+            OrchestratorName = "harness-proposer",
+            TaskDescription = BuildTaskPrompt(context),
+            AvailableAgents = BuildAgentList(_config.CurrentValue)
+        };
+
+        var result = await _mediator.Send(command, cancellationToken);
+        var proposal = ParseProposal(result.FinalSynthesis);
+
+        _logger.LogInformation(
+            "Proposer iteration {Iteration}: {SkillCount} skill change(s), {ConfigCount} config change(s), system prompt changed: {HasPromptChange}",
+            context.Iteration,
+            proposal.ProposedSkillChanges.Count,
+            proposal.ProposedConfigChanges.Count,
+            proposal.ProposedSystemPromptChange is not null);
+
+        return proposal;
+    }
+
+    private static string BuildTaskPrompt(HarnessProposerContext context)
+    {
+        var priorIds = context.PriorCandidateIds.Count > 0
+            ? string.Join(", ", context.PriorCandidateIds.Select(id => id.ToString("N")[..8]))
+            : "(none — this is the first iteration)";
+
+        return $"""
+            Optimization run directory: {context.OptimizationRunDirectoryPath}
+            Current iteration: {context.Iteration}
+            Current candidate ID: {context.CurrentCandidate.CandidateId:N}
+            Prior candidate IDs (oldest first, short form): {priorIds}
+
+            Analyze the execution traces in the candidates/ subdirectory and propose targeted
+            harness improvements. Respond with a single JSON object only (no markdown fences,
+            no preamble text).
+            """;
+    }
+
+    private static IReadOnlyList<string> BuildAgentList(MetaHarnessConfig cfg)
+    {
+        var agents = new List<string> { "file_system", "read_history" };
+
+        if (cfg.EnableShellTool)
+            agents.Add("restricted_search");
+
+        return agents;
+    }
+
+    private HarnessProposal ParseProposal(string rawOutput)
+    {
+        var start = rawOutput.IndexOf('{');
+        var end = rawOutput.LastIndexOf('}');
+
+        if (start < 0 || end <= start)
+            throw new HarnessProposalParsingException(rawOutput);
+
+        var json = rawOutput[start..(end + 1)];
+
+        JsonDocument doc;
+        try
+        {
+            doc = JsonDocument.Parse(json);
+        }
+        catch (JsonException ex)
+        {
+            throw new HarnessProposalParsingException(rawOutput, inner: ex);
+        }
+
+        using (doc)
+        {
+            var root = doc.RootElement;
+
+            var reasoning = root.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String
+                ? r.GetString() ?? ""
+                : "";
+
+            var skillChanges = ReadStringDict(root, "proposed_skill_changes");
+            var configChanges = ReadStringDict(root, "proposed_config_changes");
+            var promptChange = root.TryGetProperty("proposed_system_prompt_change", out var sp)
+                               && sp.ValueKind == JsonValueKind.String
+                ? sp.GetString()
+                : null;
+
+            return new HarnessProposal
+            {
+                Reasoning = reasoning,
+                ProposedSkillChanges = skillChanges,
+                ProposedConfigChanges = configChanges,
+                ProposedSystemPromptChange = promptChange
+            };
+        }
+    }
+
+    private static IReadOnlyDictionary<string, string> ReadStringDict(JsonElement root, string key)
+    {
+        if (!root.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.Object)
+            return new Dictionary<string, string>();
+
+        return prop.EnumerateObject()
+            .Where(p => p.Value.ValueKind == JsonValueKind.String)
+            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/OrchestratedHarnessProposerTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/OrchestratedHarnessProposerTests.cs
new file mode 100644
index 0000000..0338279
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/OrchestratedHarnessProposerTests.cs
@@ -0,0 +1,144 @@
+using Application.AI.Common.Exceptions;
+using Application.Core.CQRS.Agents.RunOrchestratedTask;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using Infrastructure.AI.MetaHarness;
+using MediatR;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Moq;
+
+namespace Infrastructure.AI.Tests.MetaHarness;
+
+/// <summary>
+/// Tests for OrchestratedHarnessProposer JSON extraction and error handling.
+/// Uses a mock IMediator that returns scripted agent output strings.
+/// </summary>
+public class OrchestratedHarnessProposerTests
+{
+    private readonly Mock<IMediator> _mediatorMock = new();
+    private readonly MetaHarnessConfig _config = new();
+
+    private OrchestratedHarnessProposer BuildSut()
+    {
+        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == _config);
+        return new OrchestratedHarnessProposer(
+            _mediatorMock.Object,
+            opts,
+            NullLogger<OrchestratedHarnessProposer>.Instance);
+    }
+
+    private static HarnessProposerContext BuildContext() => new()
+    {
+        CurrentCandidate = new HarnessCandidate
+        {
+            CandidateId = Guid.NewGuid(),
+            OptimizationRunId = Guid.NewGuid(),
+            Iteration = 0,
+            CreatedAt = DateTimeOffset.UtcNow,
+            Snapshot = new HarnessSnapshot
+            {
+                SkillFileSnapshots = new Dictionary<string, string>(),
+                SystemPromptSnapshot = "",
+                ConfigSnapshot = new Dictionary<string, string>(),
+                SnapshotManifest = []
+            },
+            Status = HarnessCandidateStatus.Evaluated
+        },
+        OptimizationRunDirectoryPath = Path.GetTempPath(),
+        PriorCandidateIds = [],
+        Iteration = 1
+    };
+
+    private void SetupMediatorResult(string finalSynthesis)
+    {
+        _mediatorMock
+            .Setup(m => m.Send(It.IsAny<RunOrchestratedTaskCommand>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new OrchestratedTaskResult
+            {
+                Success = true,
+                FinalSynthesis = finalSynthesis,
+                SubAgentResults = []
+            });
+    }
+
+    /// <summary>
+    /// When the agent returns a string containing a valid JSON block, ProposeAsync
+    /// should extract the first '{' to last '}' substring, parse it, and return a
+    /// populated HarnessProposal.
+    /// </summary>
+    [Fact]
+    public async Task ProposeAsync_ValidJsonBlock_ReturnsParsedProposal()
+    {
+        const string agentOutput = """
+            Here is my analysis.
+            {
+              "reasoning": "Need to improve skill clarity.",
+              "proposed_skill_changes": { "skills/harness-proposer/SKILL.md": "# Updated" },
+              "proposed_config_changes": { "MetaHarness:MaxIterations": "5" },
+              "proposed_system_prompt_change": null
+            }
+            Let me know if you need more details.
+            """;
+        SetupMediatorResult(agentOutput);
+
+        var result = await BuildSut().ProposeAsync(BuildContext(), CancellationToken.None);
+
+        Assert.Equal("Need to improve skill clarity.", result.Reasoning);
+        Assert.Single(result.ProposedSkillChanges);
+        Assert.Equal("# Updated", result.ProposedSkillChanges["skills/harness-proposer/SKILL.md"]);
+        Assert.Single(result.ProposedConfigChanges);
+        Assert.Equal("5", result.ProposedConfigChanges["MetaHarness:MaxIterations"]);
+        Assert.Null(result.ProposedSystemPromptChange);
+    }
+
+    /// <summary>
+    /// When the agent returns text that contains no valid JSON object (no matching
+    /// braces), ProposeAsync should throw HarnessProposalParsingException with the
+    /// raw output included in the exception message.
+    /// </summary>
+    [Fact]
+    public async Task ProposeAsync_InvalidJsonOutput_ThrowsHarnessProposalParsingException()
+    {
+        SetupMediatorResult("No JSON here, sorry.");
+
+        var ex = await Assert.ThrowsAsync<HarnessProposalParsingException>(
+            () => BuildSut().ProposeAsync(BuildContext(), CancellationToken.None));
+
+        Assert.Contains("No JSON here, sorry.", ex.RawOutput);
+    }
+
+    /// <summary>
+    /// When the JSON block is valid but ProposedSkillChanges and ProposedConfigChanges
+    /// are absent or empty, ProposeAsync should return a HarnessProposal with empty
+    /// dictionaries (not null) and a non-null Reasoning string.
+    /// </summary>
+    [Fact]
+    public async Task ProposeAsync_EmptyProposedChanges_ReturnsProposalWithEmptyDicts()
+    {
+        SetupMediatorResult("""{"reasoning": "Nothing to change yet."}""");
+
+        var result = await BuildSut().ProposeAsync(BuildContext(), CancellationToken.None);
+
+        Assert.Equal("Nothing to change yet.", result.Reasoning);
+        Assert.Empty(result.ProposedSkillChanges);
+        Assert.Empty(result.ProposedConfigChanges);
+        Assert.Null(result.ProposedSystemPromptChange);
+    }
+
+    /// <summary>
+    /// When the JSON block includes a "reasoning" field, its value should be surfaced
+    /// on HarnessProposal.Reasoning verbatim.
+    /// </summary>
+    [Fact]
+    public async Task ProposeAsync_ProposalContainsReasoning_ReasoningPassedThrough()
+    {
+        const string reasoning = "The agent missed tool selection on 3 out of 5 tasks.";
+        SetupMediatorResult(
+            $$"""{"reasoning": "{{reasoning}}", "proposed_skill_changes": {}, "proposed_config_changes": {}}""");
+
+        var result = await BuildSut().ProposeAsync(BuildContext(), CancellationToken.None);
+
+        Assert.Equal(reasoning, result.Reasoning);
+    }
+}
