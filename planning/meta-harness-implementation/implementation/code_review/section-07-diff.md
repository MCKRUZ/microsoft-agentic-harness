diff --git a/skills/harness-proposer/SKILL.md b/skills/harness-proposer/SKILL.md
new file mode 100644
index 0000000..44dee2c
--- /dev/null
+++ b/skills/harness-proposer/SKILL.md
@@ -0,0 +1,115 @@
+---
+name: "harness-proposer"
+description: "Reads execution traces and proposes skill/prompt changes to improve agent performance."
+category: "meta"
+skill_type: "orchestration"
+version: "1.0.0"
+tags: ["meta", "optimization", "harness"]
+allowed-tools: ["file_system", "read_history"]
+---
+
+You are the harness proposer — a meta-agent that analyzes execution traces from previous agent runs and proposes targeted changes to skill instructions or system prompts to improve performance.
+
+## Instructions
+
+Your job is to close the loop between agent execution and agent improvement. You read trace data, identify failure patterns, and produce concrete, actionable proposals for modifying skill files.
+
+### Process
+
+1. Use `read_history` to retrieve recent agent execution history for context on past runs
+2. Use `file_system` to read trace files from the execution trace directory (see ## Trace Format below)
+3. Analyze `traces.jsonl` for tool call patterns, error rates, and decision paths
+4. Analyze `decisions.jsonl` for evaluation outcomes and failure reasons
+5. Read `manifest.json` to understand run metadata (model, skill, candidate, timestamp)
+6. Identify the highest-impact failure pattern across the run set
+7. Propose a specific, targeted change to the skill's `## Instructions` section
+8. Output the proposal in structured format: problem, evidence, proposed change, expected impact
+
+### Proposal Format
+
+```
+## Proposal
+
+**Problem:** <one sentence describing the failure pattern>
+**Evidence:** <file path + trace line(s) that demonstrate the problem>
+**Proposed change:** <exact diff or replacement text for the skill section>
+**Expected impact:** <which eval tasks should improve and why>
+```
+
+### Constraints
+
+- Propose one change per run — the most impactful one
+- Changes must be grounded in trace evidence, not speculation
+- Do not propose changes to frontmatter fields (name, tags, allowed-tools)
+- Do not propose adding tools not already in `allowed-tools`
+
+## Objectives
+
+- Improve pass rate on evaluator tasks by identifying and fixing the root cause of the most common failure pattern
+- Reduce token cost per successful task by eliminating unnecessary tool calls visible in trace data
+- Identify failure patterns from execution traces with specificity (not "agent failed" but "agent called file_system with path '.' causing search exhaustion")
+- Propose targeted changes to skill instructions or system prompts that address root causes, not symptoms
+
+## Trace Format
+
+Execution traces are written by `FileSystemExecutionTraceStore` to a configurable base directory. The layout is:
+
+```
+{base_path}/
+  {run_id}/                   ← one directory per optimization run (UUID)
+    manifest.json             ← run metadata: model, skill_id, candidate_id, started_at, status
+    traces.jsonl              ← append-only log of ExecutionTraceEntry records (one JSON object per line)
+    decisions.jsonl           ← append-only log of EvaluationDecision records (one JSON object per line)
+    candidates/
+      {candidate_id}/         ← one directory per skill candidate evaluated in this run
+        skill.md              ← the candidate skill content that was evaluated
+        result.json           ← evaluation result: score, pass/fail, token_count, latency_ms
+```
+
+**traces.jsonl schema (one object per line):**
+```json
+{
+  "trace_id": "uuid",
+  "run_id": "uuid",
+  "timestamp": "2025-01-01T00:00:00Z",
+  "agent_id": "string",
+  "event_type": "tool_call | decision | message | error",
+  "tool_name": "string | null",
+  "input": "string | null",
+  "output": "string | null",
+  "duration_ms": 0,
+  "token_count": 0,
+  "span_id": "string | null",
+  "parent_span_id": "string | null"
+}
+```
+
+**decisions.jsonl schema (one object per line):**
+```json
+{
+  "decision_id": "uuid",
+  "run_id": "uuid",
+  "candidate_id": "uuid",
+  "timestamp": "2025-01-01T00:00:00Z",
+  "task_id": "string",
+  "passed": true,
+  "score": 0.0,
+  "failure_reason": "string | null",
+  "evaluator_notes": "string | null"
+}
+```
+
+**manifest.json schema:**
+```json
+{
+  "run_id": "uuid",
+  "skill_id": "string",
+  "base_candidate_id": "uuid",
+  "started_at": "2025-01-01T00:00:00Z",
+  "completed_at": "2025-01-01T00:00:00Z | null",
+  "status": "running | completed | failed",
+  "model": "string",
+  "total_candidates": 0,
+  "passed_candidates": 0
+}
+```
diff --git a/skills/research-agent/SKILL.md b/skills/research-agent/SKILL.md
index 390fe82..ae3afee 100644
--- a/skills/research-agent/SKILL.md
+++ b/skills/research-agent/SKILL.md
@@ -41,3 +41,15 @@ The project root is the working directory. **Always start searches from `src`**
 - Prefer reading the actual implementation over summaries or docs
 - Structure findings clearly with headers and bullet points
 - Do not attempt to call resources that are not listed in the `tools` section above
+
+## Objectives
+
+- Locate the specific information requested — files, classes, methods, config values, or patterns
+- Return exact file paths and line numbers where applicable
+- Identify uncertainty explicitly rather than speculating
+- Minimize tool calls: prefer targeted searches over broad directory listings
+
+## Trace Format
+
+Not applicable — this skill does not produce structured traces. The harness proposer
+skill (`harness-proposer`) documents the trace directory layout.
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/ISkillMetadataRegistry.cs b/src/Content/Application/Application.AI.Common/Interfaces/ISkillMetadataRegistry.cs
index 7eaa5ef..5056ca9 100644
--- a/src/Content/Application/Application.AI.Common/Interfaces/ISkillMetadataRegistry.cs
+++ b/src/Content/Application/Application.AI.Common/Interfaces/ISkillMetadataRegistry.cs
@@ -33,6 +33,11 @@ public interface ISkillMetadataRegistry
     /// </summary>
     IReadOnlyList<SkillDefinition> GetByTags(IEnumerable<string> tags);
 
+    /// <summary>
+    /// Returns skills matching the given skill type (e.g., "orchestration", "analysis").
+    /// </summary>
+    IReadOnlyList<SkillDefinition> GetBySkillType(string skillType);
+
     /// <summary>
     /// Returns the filesystem paths that were searched during discovery.
     /// </summary>
diff --git a/src/Content/Domain/Domain.AI/Skills/SkillDefinition.cs b/src/Content/Domain/Domain.AI/Skills/SkillDefinition.cs
index 6204615..a1e5d2f 100644
--- a/src/Content/Domain/Domain.AI/Skills/SkillDefinition.cs
+++ b/src/Content/Domain/Domain.AI/Skills/SkillDefinition.cs
@@ -46,11 +46,25 @@ public class SkillDefinition
 	#region Level 2: Folder (Instructions — On Demand)
 
 	/// <summary>
-	/// Full instruction content (markdown body after frontmatter).
+	/// Full instruction content (markdown body after frontmatter, with structured sections removed).
 	/// Becomes the agent's system prompt. Target: ~5,000 tokens.
 	/// </summary>
 	public string? Instructions { get; set; } = string.Empty;
 
+	/// <summary>
+	/// Structured objectives extracted from the ## Objectives section of SKILL.md.
+	/// Surfaces success criteria, failure patterns, and trade-offs for the agent.
+	/// Null when the section is absent (backward compatible).
+	/// </summary>
+	public string? Objectives { get; set; }
+
+	/// <summary>
+	/// Trace directory layout documentation extracted from the ## Trace Format section of SKILL.md.
+	/// Used by the harness proposer to navigate execution trace directories.
+	/// Null when the section is absent (backward compatible).
+	/// </summary>
+	public string? TraceFormat { get; set; }
+
 	#endregion
 
 	#region Categorization
@@ -219,6 +233,8 @@ public class SkillDefinition
 
 	#region Computed Properties
 
+	public bool HasObjectives => !string.IsNullOrWhiteSpace(Objectives);
+	public bool HasTraceFormat => !string.IsNullOrWhiteSpace(TraceFormat);
 	public bool HasTemplates => Templates.Count > 0;
 	public bool HasReferences => References.Count > 0;
 	public bool HasScripts => Scripts?.Count > 0;
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Skills/SkillMetadataParser.cs b/src/Content/Infrastructure/Infrastructure.AI/Skills/SkillMetadataParser.cs
index c1db9c2..d66781c 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/Skills/SkillMetadataParser.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/Skills/SkillMetadataParser.cs
@@ -33,6 +33,10 @@ public sealed class SkillMetadataParser
         var frontmatter = ExtractFrontmatter(raw);
         var body = ExtractBody(raw, frontmatter);
 
+        var objectives = ExtractSection(body, "Objectives");
+        var traceFormat = ExtractSection(body, "Trace Format");
+        var instructions = StripSections(body, "Objectives", "Trace Format");
+
         var name = ParseString(frontmatter, "name") ?? Path.GetFileName(sourcePath);
         var description = ParseString(frontmatter, "description") ?? string.Empty;
 
@@ -41,7 +45,9 @@ public sealed class SkillMetadataParser
             Id = name,
             Name = name,
             Description = description,
-            Instructions = body,
+            Instructions = instructions,
+            Objectives = objectives,
+            TraceFormat = traceFormat,
             Category = ParseString(frontmatter, "category"),
             SkillType = ParseString(frontmatter, "skill_type"),
             Version = ParseString(frontmatter, "version"),
@@ -81,12 +87,18 @@ public sealed class SkillMetadataParser
             _logger.LogWarning(ex, "Could not read custom frontmatter from {Path}", skillFilePath);
         }
 
+        var objectives = ExtractSection(body, "Objectives");
+        var traceFormat = ExtractSection(body, "Trace Format");
+        var instructions = StripSections(body, "Objectives", "Trace Format");
+
         return new SkillDefinition
         {
             Id = skillName,
             Name = skillName,
             Description = skillDescription ?? string.Empty,
-            Instructions = body,
+            Instructions = instructions,
+            Objectives = objectives,
+            TraceFormat = traceFormat,
             Category = ParseString(rawFrontmatter, "category"),
             SkillType = ParseString(rawFrontmatter, "skill_type"),
             Version = ParseString(rawFrontmatter, "version"),
@@ -124,6 +136,93 @@ public sealed class SkillMetadataParser
         return bodyStart >= raw.Length ? string.Empty : raw[bodyStart..].Trim();
     }
 
+    /// <summary>
+    /// Extracts the content of a named ## Heading section from a markdown body.
+    /// Returns null if the heading is not present. Content ends at the next ## heading or EOF.
+    /// Matching is case-insensitive; extracted content is trimmed.
+    /// </summary>
+    private static string? ExtractSection(string body, string heading)
+    {
+        var lines = body.Split('\n');
+        var searchHeading = $"## {heading}";
+
+        var startIdx = -1;
+        for (var i = 0; i < lines.Length; i++)
+        {
+            if (lines[i].Trim().Equals(searchHeading, StringComparison.OrdinalIgnoreCase))
+            {
+                startIdx = i;
+                break;
+            }
+        }
+
+        if (startIdx < 0)
+            return null;
+
+        var endIdx = lines.Length;
+        for (var i = startIdx + 1; i < lines.Length; i++)
+        {
+            if (lines[i].TrimStart().StartsWith("## ", StringComparison.Ordinal))
+            {
+                endIdx = i;
+                break;
+            }
+        }
+
+        var content = string.Join('\n', lines[(startIdx + 1)..endIdx]).Trim();
+        return string.IsNullOrWhiteSpace(content) ? null : content;
+    }
+
+    /// <summary>
+    /// Returns the body with the specified ## Heading sections removed.
+    /// Consecutive blank lines left by removal are collapsed to at most one.
+    /// </summary>
+    private static string StripSections(string body, params string[] headings)
+    {
+        var headingSet = new HashSet<string>(
+            headings.Select(h => $"## {h}"),
+            StringComparer.OrdinalIgnoreCase);
+
+        var lines = body.Split('\n');
+        var result = new List<string>(lines.Length);
+        var skipping = false;
+
+        foreach (var line in lines)
+        {
+            if (headingSet.Contains(line.Trim()))
+            {
+                skipping = true;
+                continue;
+            }
+
+            if (skipping && line.TrimStart().StartsWith("## ", StringComparison.Ordinal))
+                skipping = false;
+
+            if (!skipping)
+                result.Add(line);
+        }
+
+        // Collapse runs of blank lines to at most one
+        var normalized = new List<string>(result.Count);
+        var blankRun = 0;
+        foreach (var line in result)
+        {
+            if (string.IsNullOrWhiteSpace(line))
+            {
+                blankRun++;
+                if (blankRun <= 1)
+                    normalized.Add(line);
+            }
+            else
+            {
+                blankRun = 0;
+                normalized.Add(line);
+            }
+        }
+
+        return string.Join('\n', normalized).Trim();
+    }
+
     private static string? ParseString(string? frontmatter, string key)
     {
         if (string.IsNullOrEmpty(frontmatter))
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Skills/SkillMetadataRegistry.cs b/src/Content/Infrastructure/Infrastructure.AI/Skills/SkillMetadataRegistry.cs
index abbf598..d6ee5eb 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/Skills/SkillMetadataRegistry.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/Skills/SkillMetadataRegistry.cs
@@ -78,6 +78,15 @@ public sealed class SkillMetadataRegistry : ISkillMetadataRegistry
             .ToList();
     }
 
+    /// <inheritdoc />
+    public IReadOnlyList<SkillDefinition> GetBySkillType(string skillType)
+    {
+        EnsureLoaded();
+        return _cache!.Values
+            .Where(s => string.Equals(s.SkillType, skillType, StringComparison.OrdinalIgnoreCase))
+            .ToList();
+    }
+
     private void EnsureLoaded()
     {
         if (_cache is not null)
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillMetadataRegistryTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillMetadataRegistryTests.cs
index 73d957e..8263dbd 100644
--- a/src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillMetadataRegistryTests.cs
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillMetadataRegistryTests.cs
@@ -143,6 +143,38 @@ public sealed class SkillMetadataRegistryTests
         registry.Should().NotBeNull();
     }
 
+    [Fact]
+    public void SkillMetadataRegistry_IncludesObjectives_InReturnedSkillDefinition()
+    {
+        if (!Directory.Exists(SkillsPath))
+            return;
+
+        var registry = CreateRegistry();
+
+        var skill = registry.TryGet("harness-proposer");
+
+        skill.Should().NotBeNull("harness-proposer SKILL.md includes ## Objectives");
+        skill!.Objectives.Should().NotBeNullOrWhiteSpace();
+        skill.HasObjectives.Should().BeTrue();
+    }
+
+    [Fact]
+    public void SkillMetadataRegistry_ExistingSkillsWithoutNewSections_ParseCorrectly()
+    {
+        if (!Directory.Exists(SkillsPath))
+            return;
+
+        var registry = CreateRegistry();
+
+        // orchestrator-agent has no ## Objectives or ## Trace Format — should parse without error
+        var skill = registry.TryGet("orchestrator-agent");
+
+        skill.Should().NotBeNull();
+        skill!.Objectives.Should().BeNull();
+        skill.TraceFormat.Should().BeNull();
+        skill.Instructions.Should().NotBeNullOrWhiteSpace();
+    }
+
     private sealed class OptionsMonitorStub : IOptionsMonitor<AppConfig>
     {
         public OptionsMonitorStub(AppConfig value) => CurrentValue = value;
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillParserExtensionTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillParserExtensionTests.cs
new file mode 100644
index 0000000..96d5a97
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillParserExtensionTests.cs
@@ -0,0 +1,141 @@
+using FluentAssertions;
+using Infrastructure.AI.Skills;
+using Microsoft.Extensions.Logging.Abstractions;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Skills;
+
+/// <summary>
+/// Tests for SkillMetadataParser extraction of ## Objectives and ## Trace Format sections.
+/// </summary>
+public sealed class SkillParserExtensionTests
+{
+    private static SkillMetadataParser CreateParser() =>
+        new(NullLogger<SkillMetadataParser>.Instance);
+
+    [Fact]
+    public void SkillParser_WithObjectivesSection_ExtractsObjectivesContent()
+    {
+        var parser = CreateParser();
+        const string body = """
+
+            ## Instructions
+
+            Do the thing.
+
+            ## Objectives
+
+            - Succeed at the thing.
+
+            """;
+
+        using var dir = new TempDirectory();
+        var skill = parser.Parse("test-skill", "A test skill", body, dir.Path);
+
+        skill.Objectives.Should().NotBeNullOrWhiteSpace();
+        skill.Objectives.Should().Contain("Succeed at the thing");
+    }
+
+    [Fact]
+    public void SkillParser_WithTraceFormatSection_ExtractsTraceFormatContent()
+    {
+        var parser = CreateParser();
+        const string body = """
+
+            ## Instructions
+
+            Do the thing.
+
+            ## Trace Format
+
+            Traces live under traces/{run_id}/.
+
+            """;
+
+        using var dir = new TempDirectory();
+        var skill = parser.Parse("test-skill", "A test skill", body, dir.Path);
+
+        skill.TraceFormat.Should().NotBeNullOrWhiteSpace();
+        skill.TraceFormat.Should().Contain("traces/{run_id}");
+    }
+
+    [Fact]
+    public void SkillParser_WithoutObjectivesSection_ReturnsNullObjectives()
+    {
+        var parser = CreateParser();
+        const string body = """
+
+            ## Instructions
+
+            Do the thing.
+
+            """;
+
+        using var dir = new TempDirectory();
+        var skill = parser.Parse("test-skill", "A test skill", body, dir.Path);
+
+        skill.Objectives.Should().BeNull();
+    }
+
+    [Fact]
+    public void SkillParser_WithoutTraceFormatSection_ReturnsNullTraceFormat()
+    {
+        var parser = CreateParser();
+        const string body = """
+
+            ## Instructions
+
+            Do the thing.
+
+            """;
+
+        using var dir = new TempDirectory();
+        var skill = parser.Parse("test-skill", "A test skill", body, dir.Path);
+
+        skill.TraceFormat.Should().BeNull();
+    }
+
+    [Fact]
+    public void SkillParser_ExtractedSections_AreRemovedFromInstructions()
+    {
+        var parser = CreateParser();
+        const string body = """
+
+            ## Instructions
+
+            Do the thing.
+
+            ## Objectives
+
+            - Succeed at the thing.
+
+            ## Trace Format
+
+            Traces live under traces/{run_id}/.
+
+            """;
+
+        using var dir = new TempDirectory();
+        var skill = parser.Parse("test-skill", "A test skill", body, dir.Path);
+
+        skill.Instructions.Should().NotContain("## Objectives");
+        skill.Instructions.Should().NotContain("## Trace Format");
+        skill.Instructions.Should().NotContain("Succeed at the thing");
+        skill.Instructions.Should().NotContain("traces/{run_id}");
+    }
+
+    private sealed class TempDirectory : IDisposable
+    {
+        public string Path { get; } = System.IO.Path.Combine(
+            System.IO.Path.GetTempPath(),
+            System.IO.Path.GetRandomFileName());
+
+        public TempDirectory() => Directory.CreateDirectory(Path);
+
+        public void Dispose()
+        {
+            if (Directory.Exists(Path))
+                Directory.Delete(Path, recursive: true);
+        }
+    }
+}
