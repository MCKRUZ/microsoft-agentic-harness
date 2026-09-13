using System.Text.RegularExpressions;
using Application.AI.Common.Extensions;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Services.Governance;
using Application.Common.Helpers;
using Domain.AI.Agents;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.Helpers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.MetaHarness;

/// <summary>
/// Evaluates a harness candidate by running each eval task against the candidate's
/// in-memory skill snapshot, grading outputs via regex, and writing per-task traces.
/// </summary>
/// <remarks>
/// Registered as <c>Scoped</c> — each evaluation creates its own <see cref="SemaphoreSlim"/>
/// scoped to the current optimization loop iteration.
/// </remarks>
public sealed class AgentEvaluationService : IEvaluationService
{
    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
    private readonly IExecutionTraceStore _traceStore;
    private readonly IAgentFactory _agentFactory;
    private readonly IToolCallAdmissionPipeline _admissionPipeline;
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentEvaluationService> _logger;

    // Candidate-proposed scripts are never executed during evaluation — running untrusted
    // LLM-authored scripts would be an RCE vector. Mirrors AgentExecutionContextFactory.
    private static readonly AgentFileSkillScriptRunner NoOpScriptRunner =
        (skill, script, arguments, serviceProvider, cancellationToken) =>
            Task.FromResult<object?>(null);

    /// <param name="admissionPipeline">
    /// The scoped admission chain armed around every eval run's <c>agent.RunAsync</c> call (#482) —
    /// without it, <c>GovernedAIFunction</c>'s null-ambient early-return path leaves any tool a
    /// candidate-loaded skill declares completely ungoverned, the same gap #478 closed for
    /// <c>Presentation.FoundryHost</c>.
    /// </param>
    /// <param name="sanitizer">
    /// Passed to the <see cref="Application.AI.Common.Services.Agent.GoverningToolContextProvider"/> this service now wires onto its own
    /// context (#482), matching how <c>AgentExecutionContextFactory</c> wires it for every production
    /// agent — otherwise eval's skill-disclosure tools (<c>load_skill</c>/<c>read_skill_resource</c>)
    /// carry no sanitize coverage at all.
    /// </param>
    public AgentEvaluationService(
        IOptionsMonitor<MetaHarnessConfig> config,
        IExecutionTraceStore traceStore,
        IAgentFactory agentFactory,
        IToolCallAdmissionPipeline admissionPipeline,
        ICompositeResponseSanitizer sanitizer,
        ILoggerFactory loggerFactory,
        ILogger<AgentEvaluationService> logger)
    {
        _config = config;
        _traceStore = traceStore;
        _agentFactory = agentFactory;
        _admissionPipeline = admissionPipeline;
        _sanitizer = sanitizer;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EvaluationResult> EvaluateAsync(
        HarnessCandidate candidate,
        IReadOnlyList<EvalTask> evalTasks,
        CancellationToken cancellationToken = default)
    {
        var cfg = _config.CurrentValue;
        var parallelism = Math.Max(1, cfg.MaxEvalParallelism);
        using var semaphore = new SemaphoreSlim(parallelism, parallelism);

        var taskResults = await Task.WhenAll(
            evalTasks.Select(task => RunSingleTaskAsync(candidate, task, cfg, semaphore, cancellationToken)));

        var passed = taskResults.Count(r => r.Passed);
        var passRate = evalTasks.Count > 0 ? (double)passed / evalTasks.Count : 0.0;
        var totalTokenCost = taskResults.Sum(r => r.TokenCost);

        _logger.LogInformation(
            "Candidate {CandidateId}: {Passed}/{Total} tasks passed (PassRate={PassRate:F2})",
            candidate.CandidateId, passed, evalTasks.Count, passRate);

        return new EvaluationResult(candidate.CandidateId, passRate, totalTokenCost, taskResults);
    }

    private async Task<TaskEvaluationResult> RunSingleTaskAsync(
        HarnessCandidate candidate,
        EvalTask task,
        MetaHarnessConfig cfg,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteTaskAsync(candidate, task, cfg, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<TaskEvaluationResult> ExecuteTaskAsync(
        HarnessCandidate candidate,
        EvalTask task,
        MetaHarnessConfig cfg,
        CancellationToken cancellationToken)
    {
        var scope = new TraceScope
        {
            ExecutionRunId = Guid.NewGuid(),
            OptimizationRunId = candidate.OptimizationRunId,
            CandidateId = candidate.CandidateId,
            TaskId = task.TaskId
        };

        var metadata = new RunMetadata
        {
            AgentName = "EvaluationAgent",
            StartedAt = DateTimeOffset.UtcNow
        };

        ITraceWriter? traceWriter = null;
        var traceCompleted = false;
        TaskEvaluationResult? taskResult = null;
        string? skillDirectory = null;

        try
        {
            traceWriter = await _traceStore.StartRunAsync(scope, metadata, cancellationToken);

            // Materialize the candidate's proposed skills so the eval agent loads them through the
            // same MAF progressive-disclosure path used in production. Without this, a candidate that
            // changes only skill files would evaluate identically to its parent.
            skillDirectory = MaterializeCandidateSkills(candidate.Snapshot, scope.ExecutionRunId);

            var context = new AgentExecutionContext
            {
                Name = "EvaluationAgent",
                Instruction = candidate.Snapshot.SystemPromptSnapshot,
                DeploymentName = string.IsNullOrEmpty(cfg.EvaluationModelVersion) ? null : cfg.EvaluationModelVersion,
                TraceScope = scope,
                AIContextProviders = BuildContextProviders(skillDirectory),
                AdditionalProperties = new Dictionary<string, object>
                {
                    [ITraceWriter.AdditionalPropertiesKey] = traceWriter
                }
            };

            var agent = await _agentFactory.CreateAgentAsync(context, cancellationToken);

            // #482: arm the same ambient admission chain ExecuteAgentTurnCommandHandler arms for every
            // production turn. Begin (not assign-and-null) so a nested/enclosing governed flow is
            // restored rather than disarmed on the way out — see ToolAdmissionAccessor's remarks.
            //
            // Reset before arming, mirroring DirectToolInvoker.ArmGovernance: this pipeline is a single
            // scoped instance shared across every eval task and candidate run in this scope (found in
            // review), so without a reset here its loop-detection and call-once state accumulates across
            // tasks — one task's tool-call pattern could trip (or silently satisfy) the loop guard for an
            // unrelated later task in the same scope.
            _admissionPipeline.Reset();

            AgentResponse response;
            using (ToolAdmissionAccessor.Begin(_admissionPipeline))
            {
                response = await agent.RunAsync(
                    [new ChatMessage(ChatRole.User, task.InputPrompt)],
                    cancellationToken: cancellationToken);
            }

            var output = ExtractContent(response);
            var (passed, failureReason) = Grade(output, task.ExpectedOutputPattern);
            taskResult = new TaskEvaluationResult(
                task.TaskId, passed, ResolveTokenCost(response, task.InputPrompt, output), failureReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Task {TaskId} failed for candidate {CandidateId}",
                task.TaskId, candidate.CandidateId);

            // Zero here is a genuine unknown, not a placeholder: without a response there is no usage to
            // read, and the throw may have come before anything reached the provider or after a full turn
            // was billed. Estimating would put an invented number into a candidate-selection input.
            taskResult = new TaskEvaluationResult(task.TaskId, Passed: false, TokenCost: 0L, ex.Message);
        }
        finally
        {
            // Complete exactly once, then dispose. Use CancellationToken.None so the manifest
            // is finalized even when the parent cancellation token is already signalled.
            if (traceWriter is not null && !traceCompleted)
            {
                try
                {
                    await traceWriter.CompleteAsync(CancellationToken.None);
                    traceCompleted = true;
                }
                catch (Exception completionEx)
                {
                    _logger.LogWarning(completionEx, "Failed to complete trace for task {TaskId}", task.TaskId);
                }

                await traceWriter.DisposeAsync();
            }

            if (skillDirectory is not null)
                TryDeleteDirectory(skillDirectory);
        }

        return taskResult!;
    }

    /// <summary>
    /// Materializes the candidate's skill snapshot to an isolated temp directory so the eval
    /// agent can load the proposed skills via MAF's <see cref="AgentSkillsProvider"/>. Returns
    /// <see langword="null"/> only when the candidate proposed no skill files at all — a genuine
    /// no-op, not a malformed one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Snapshot keys originate from LLM-authored proposals and are therefore untrusted: each path
    /// is resolved and asserted to stay within the temp root to block path-traversal escapes.
    /// Unchanged files are secret-redacted in the snapshot, but that redaction is constant across a
    /// candidate and its parent, so the comparative pass-rate signal is preserved; the proposed
    /// (changed) files are unredacted and faithfully evaluated.
    /// </para>
    /// <para>
    /// <strong>#618: the skill's own subdirectory must be named after its declared frontmatter
    /// <c>name</c>, not left as the run-scoped temp root.</strong> Verified directly against the
    /// pinned <c>Microsoft.Agents.AI</c> 1.13.0 package: <c>AgentFileSkillsSource</c> requires a
    /// discovered <c>SKILL.md</c>'s declared name to ordinal-equal its own CONTAINING directory's
    /// leaf name, logs a name/directory-mismatch warning, and silently loads zero skills otherwise —
    /// reproduced end-to-end against the real SDK before this fix (materializing directly at the
    /// run root, the previous behavior, always failed this check). Without this, every eval run that
    /// proposed a skill change silently evaluated the candidate's UNCHANGED parent skill instead —
    /// exactly what this method's own original doc comment said it existed to prevent. The run root
    /// itself stays random-GUID-named and is still what gets passed to <c>UseFileSkill</c>: the SDK
    /// treats that path as a directory to SEARCH within (confirmed: a correctly-named subdirectory
    /// nested under an arbitrarily-named parent loads correctly), not as the skill's own directory.
    /// </para>
    /// <para>
    /// A candidate that proposed skill files which cannot form a loadable skill is a malformed
    /// proposal, not a no-op one — this throws rather than returning null for that case, so the
    /// caller's task-level catch fails the task instead of silently materializing nothing and
    /// scoring the candidate identically to its unchanged parent, which is the same silent-no-op
    /// symptom #618 fixes, just reintroduced via a different malformed-input shape. The check is
    /// "does anything end up loadable" (no bare top-level <c>SKILL.md</c> AND no nested
    /// <c>"{segment}/SKILL.md"</c> whose declared name matches its own directory), not merely "does a
    /// file named <c>SKILL.md</c> exist somewhere" — an earlier version of this check used the
    /// latter, which is weaker than the admission rule the rest of the method actually applies and
    /// let a mis-named nested <c>SKILL.md</c> with no bare top-level key silently degrade to zero
    /// loaded skills; caught by CI's grader gate.
    /// </para>
    /// <para>
    /// <strong>The re-nesting decision is made PER FILE, not once for the whole snapshot —
    /// caught by CI review, twice, after two earlier versions of this fix each classified the
    /// entire snapshot as one of two shapes.</strong> A snapshot captured from ONE skill's own
    /// directory (<c>ActiveConfigSnapshotBuilder</c> pointed directly at a single skill folder) has
    /// a BARE top-level <c>SKILL.md</c> key with no name-prefixed subdirectory — this is the shape
    /// #618's bug affects, and its ENTIRE bare-rooted group (every key with no directory segment)
    /// needs re-nesting one level down under a subdirectory named after its declared frontmatter
    /// name. A snapshot captured from a MULTI-skill root (pointed at a directory containing several
    /// named skill subfolders) already has every key prefixed with its own skill's directory name
    /// (e.g. <c>"research-agent/SKILL.md"</c>) — that shape already satisfies the SDK's naming
    /// convention exactly as captured and must be materialized as-is; re-nesting it again under an
    /// additional derived name would break a layout that already materializes and loads correctly.
    /// </para>
    /// <para>
    /// Both shapes can coexist in ONE snapshot: <c>ProposeChangesExecutor.ApplyProposalToSnapshot</c>
    /// merges an LLM-authored proposal's keys into the current snapshot with no shape validation at
    /// all, so a proposal against an already-multi-skill seed can add a bare top-level
    /// <c>SKILL.md</c> alongside pre-existing <c>"research-agent/SKILL.md"</c>-shaped entries.
    /// Classifying the whole snapshot from one key (an earlier version of this fix) mis-routes the
    /// already-correct entries whenever a bare key is also present. The placement is therefore
    /// decided independently per key.
    /// </para>
    /// <para>
    /// <strong>A key having a directory segment does NOT by itself mean it belongs to an
    /// already-correct sibling skill</strong> — caught in review after the per-key version above
    /// still misrouted a real shape: the bare-rooted skill's OWN resource files (a normal skill
    /// authoring convention — <c>SkillResource.RelativePath</c> documents this as relative to "the
    /// skill's base directory", e.g. <c>"resources/notes.md"</c> or <c>"scripts/run.py"</c> sitting
    /// alongside a bare top-level <c>SKILL.md</c>) also have a directory segment, but must land
    /// INSIDE the bare skill's derived-name subdirectory, not beside it.
    /// </para>
    /// <para>
    /// <strong>Mere presence of a <c>"{segment}/SKILL.md"</c> key is not sufficient either</strong>
    /// — caught in review: checking whether that exact key exists is a tautology for the key itself
    /// (it always "contains" its own key), so it can't distinguish a genuine sibling skill from a
    /// resource file that merely happens to be named <c>SKILL.md</c> inside the bare-rooted skill's
    /// own subfolder (e.g. a template/reference resource at <c>"examples/SKILL.md"</c>). A segment is
    /// only recognized as a genuine sibling skill directory when its own <c>"{segment}/SKILL.md"</c>
    /// entry's declared frontmatter <c>name</c> is ordinal-equal to the segment itself — the SAME
    /// admission rule the real SDK applies (see <see cref="ResolveDeclaredSkillName"/> and
    /// <see cref="TryParseDeclaredName"/>), not a proxy heuristic for it. Every other key — no
    /// segment, or a segment that fails this check — belongs to the bare-rooted group and is
    /// re-nested, preserving its own relative path underneath.
    /// </para>
    /// </remarks>
    private string? MaterializeCandidateSkills(HarnessSnapshot snapshot, Guid executionRunId)
    {
        if (snapshot.SkillFileSnapshots.Count == 0)
            return null;

        // Computed BEFORE the loud-fail check below, not after: the check needs to know whether
        // anything is actually loadable, and "a file named SKILL.md exists somewhere" is not the
        // same question — caught by CI's grader gate.
        var recognizedSiblingDirs = RecognizeSiblingSkillDirectories(snapshot);
        var hasBareTopLevelSkillMd = snapshot.SkillFileSnapshots.ContainsKey("SKILL.md");
        if (!hasBareTopLevelSkillMd && recognizedSiblingDirs.Count == 0)
        {
            throw new InvalidOperationException(
                $"Candidate for execution run {executionRunId} has skill files but none form a " +
                "loadable skill: no top-level SKILL.md, and no nested SKILL.md whose declared name " +
                "matches its own directory; cannot materialize a loadable skill directory.");
        }

        // Resolved here, before anything is written — see ResolveBareSkillNameOrThrowOnCollision.
        var bareSkillName = hasBareTopLevelSkillMd
            ? ResolveBareSkillNameOrThrowOnCollision(snapshot, recognizedSiblingDirs, executionRunId)
            : null;

        // Canonicalize once so the containment check compares like-for-like (handles symlinked
        // temp roots on macOS and 8.3 short names on Windows).
        var runRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "harness-eval-skills", executionRunId.ToString("N")));

        try
        {
            // Owner-only (#660, following #640/#527's precedent): materialized under the SYSTEM
            // temp root, which is typically world-listable -- unlike the other migrated stores,
            // this one sits directly in a shared location by default, not just a configured
            // app-output directory.
            OwnerOnlyDirectoryHelper.Create(runRoot);

            // #618: the bare-rooted group's files live one level down, in a subdirectory named
            // after the declared frontmatter name — SafeResolveWithinRoot is reused here (not a new
            // sanitizer) since bareSkillName is candidate-authored, untrusted input with exactly the
            // same path-traversal risk as any other snapshot key.
            string? bareRootedGroupRoot = null;
            if (bareSkillName is not null)
            {
                bareRootedGroupRoot = SafeResolveWithinRoot(runRoot, bareSkillName);
                OwnerOnlyDirectoryHelper.Create(bareRootedGroupRoot);
            }

            foreach (var (relativePath, content) in snapshot.SkillFileSnapshots)
            {
                var normalized = relativePath.Replace('\\', '/');
                var slash = normalized.IndexOf('/');
                var topLevelSegment = slash < 0 ? null : normalized[..slash];
                var belongsToASiblingSkill = topLevelSegment is not null
                    && recognizedSiblingDirs.Contains(topLevelSegment);

                var groupRoot = !belongsToASiblingSkill && bareRootedGroupRoot is not null
                    ? bareRootedGroupRoot
                    : runRoot;

                var filePath = SafeResolveWithinRoot(groupRoot, relativePath);
                var directory = Path.GetDirectoryName(filePath);
                if (directory is not null)
                    OwnerOnlyDirectoryHelper.Create(directory);
                File.WriteAllText(filePath, content);
            }
        }
        catch
        {
            // A path-traversal rejection (or any write failure) must not leak a partial temp dir,
            // since the caller never receives the path to clean up.
            TryDeleteDirectory(runRoot);
            throw;
        }

        return runRoot;
    }

    /// <summary>
    /// Finds every top-level segment that names a genuine sibling skill directory — one whose own
    /// <c>"{segment}/SKILL.md"</c> entry declares a frontmatter <c>name</c> ordinal-equal to the
    /// segment itself, the same admission rule the real SDK applies (see
    /// <see cref="ResolveDeclaredSkillName"/>), not mere key presence. Key presence alone is a
    /// tautology for the <c>"{segment}/SKILL.md"</c> key itself (it always "contains" its own key),
    /// so it can't distinguish a genuine sibling from an unrelated resource file that merely happens
    /// to be named <c>SKILL.md</c> inside another skill's own subfolder (e.g. a template/reference
    /// resource at <c>"examples/SKILL.md"</c>) — caught in review.
    /// </summary>
    /// <remarks>
    /// A malformed nested <c>SKILL.md</c> can't be verified either way, so it's treated as "not a
    /// recognized sibling" rather than propagating the parse failure — this is advisory recognition
    /// of OTHER skills, not the bare-rooted skill's own manifest (which DOES fail loud, in
    /// <see cref="ResolveDeclaredSkillName"/>), so a malformed sibling must not widen
    /// <see cref="MaterializeCandidateSkills"/>'s failure surface into an unrelated multi-skill
    /// snapshot's other, unaffected entries — caught in correctness review.
    /// </remarks>
    private static HashSet<string> RecognizeSiblingSkillDirectories(HarnessSnapshot snapshot)
    {
        var recognized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, content) in snapshot.SkillFileSnapshots)
        {
            var normalizedKey = key.Replace('\\', '/');
            var slashIndex = normalizedKey.IndexOf('/');
            if (slashIndex < 0 || normalizedKey[(slashIndex + 1)..] != "SKILL.md")
                continue;

            var segment = normalizedKey[..slashIndex];
            string? declaredName;
            try
            {
                declaredName = TryParseDeclaredName(content);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (string.Equals(declaredName, segment, StringComparison.Ordinal))
                recognized.Add(segment);
        }

        return recognized;
    }

    /// <summary>
    /// Reads the candidate's declared skill name from its bare top-level <c>SKILL.md</c>
    /// frontmatter. The caller guarantees that key exists before calling this.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The top-level <c>SKILL.md</c>'s frontmatter declares no <c>name</c>. Thrown rather than
    /// returned as null so <see cref="MaterializeCandidateSkills"/>'s caller fails the task instead
    /// of silently materializing nothing — see the remarks on <see cref="MaterializeCandidateSkills"/>.
    /// </exception>
    private static string ResolveDeclaredSkillName(HarnessSnapshot snapshot, Guid executionRunId)
    {
        var skillMarkdown = snapshot.SkillFileSnapshots["SKILL.md"];
        var skillName = TryParseDeclaredName(skillMarkdown);
        if (string.IsNullOrWhiteSpace(skillName))
        {
            throw new InvalidOperationException(
                $"Candidate for execution run {executionRunId}'s SKILL.md declares no 'name' in its " +
                "frontmatter; cannot materialize a loadable skill directory.");
        }

        return skillName;
    }

    /// <summary>
    /// Resolves the bare-rooted skill's declared name and asserts it doesn't collide with a genuine
    /// recognized sibling of the same name.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The declared name is missing (see <see cref="ResolveDeclaredSkillName"/>), or matches a
    /// recognized sibling directory. A collision would resolve both groups to the identical path —
    /// silently merging two distinct skills' files into one directory with no error, caught by CI's
    /// grader gate.
    /// </exception>
    private static string ResolveBareSkillNameOrThrowOnCollision(
        HarnessSnapshot snapshot, IReadOnlySet<string> recognizedSiblingDirs, Guid executionRunId)
    {
        var bareSkillName = ResolveDeclaredSkillName(snapshot, executionRunId);
        if (recognizedSiblingDirs.Contains(bareSkillName))
        {
            throw new InvalidOperationException(
                $"Candidate for execution run {executionRunId}'s bare top-level SKILL.md declares " +
                $"name '{bareSkillName}', which collides with a genuine sibling skill directory of " +
                "the same name; cannot materialize an unambiguous skill directory.");
        }

        return bareSkillName;
    }

    /// <summary>
    /// Parses a SKILL.md's declared frontmatter <c>name</c>, or null when absent/blank. Shared by
    /// <see cref="ResolveDeclaredSkillName"/> and <see cref="MaterializeCandidateSkills"/>'s
    /// sibling-skill recognition, which both need the identical admission rule the real SDK applies.
    /// </summary>
    private static string? TryParseDeclaredName(string skillMarkdown)
    {
        var (yaml, _) = YamlFrontmatterHelper.ExtractFrontmatter(skillMarkdown);
        return Infrastructure.AI.Skills.SkillFrontmatter.Load(yaml).String("name");
    }

    /// <summary>
    /// Builds the eval context's <see cref="AIContextProvider"/> rail: the progressive-disclosure skills
    /// provider over <paramref name="skillDirectory"/> (when present) followed unconditionally by
    /// <see cref="Application.AI.Common.Services.Agent.GoverningToolContextProvider"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #482: the governance wrapper is attached unconditionally, same as the production factory —
    /// without it, <c>load_skill</c>/<c>read_skill_resource</c> carry no sanitize coverage on this path
    /// at all, since <c>ToolChainBuilder</c> (the other place governance gets wired in) is never
    /// consulted for an eval context built directly from a materialized skill snapshot.
    /// </para>
    /// <para>
    /// This does NOT fully mirror <c>AgentExecutionContextFactory.BuildMergedAIContextProviders</c>'s
    /// wiring (#589): production passes <c>disclosableSkills</c> and an <see
    /// cref="Application.AI.Common.Interfaces.Skills.ICurrentSkillAccessor"/> so <c>run_skill_script</c>
    /// can resolve which skill's egress scope a call belongs to. Neither is available here — this method
    /// loads the candidate's skill straight from a materialized directory via <c>UseFileSkill</c> rather
    /// than through <c>DisclosableSkillFactory</c>, so there is no harness <c>SkillId</c> for a candidate
    /// under evaluation to hand the resolver in the first place; a candidate is a proposed skill mutation,
    /// never a registered entry in <see cref="Application.AI.Common.Interfaces.ISkillMetadataRegistry"/>.
    /// Closing this gap for real needs eval to parse the materialized directory into a
    /// <see cref="Domain.AI.Skills.SkillDefinition"/> and read it through a sandboxed
    /// <see cref="Application.AI.Common.Interfaces.Skills.ISkillFileReader"/>, the same as the production
    /// path — tracked as a follow-up rather than folded into #589, since #589 is scoped to the production
    /// wiring. In practice this path is no worse than production today: both wire
    /// <c>UseFileScriptRunner(NoOpScriptRunner)</c>, so <c>run_skill_script</c> has nothing to run either
    /// way.
    /// </para>
    /// </remarks>
    private IList<AIContextProvider> BuildContextProviders(string? skillDirectory)
    {
        var providers = new List<AIContextProvider>();

        if (skillDirectory is not null)
        {
            providers.Add(new AgentSkillsProviderBuilder()
                .UseFileScriptRunner(NoOpScriptRunner)
                .UseOptions(SkillDisclosureDefaults.Configure)
                .UseFileSkill(skillDirectory)
                .Build());
        }

        providers.Add(new Application.AI.Common.Services.Agent.GoverningToolContextProvider(
            _loggerFactory.CreateLogger<Application.AI.Common.Services.Agent.GoverningToolContextProvider>(), _sanitizer));

        return providers;
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under the canonical <paramref name="root"/> and asserts
    /// the result stays within it. Throws on path-traversal attempts in untrusted snapshot keys.
    /// </summary>
    /// <remarks>
    /// Containment is checked via <see cref="Path.GetRelativePath(string, string)"/>, which honors the
    /// host platform's path-case rules (case-insensitive on Windows, case-sensitive on Linux) — unlike a
    /// hard-coded ordinal/ignore-case string prefix check, which is wrong on at least one platform.
    /// </remarks>
    private static string SafeResolveWithinRoot(string root, string relativePath)
    {
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, resolved);

        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException(
                $"Candidate skill path '{relativePath}' resolves outside the eval skill directory.");
        }
        return resolved;
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up eval skill directory {Directory}", directory);
        }
    }

    private static (bool Passed, string? FailureReason) Grade(string output, string? pattern)
    {
        if (pattern is null)
            return (true, null);

        try
        {
            var match = Regex.Match(output, pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
            return match.Success ? (true, null) : (false, "pattern_not_matched");
        }
        catch (RegexMatchTimeoutException)
        {
            return (false, "regex_timeout");
        }
    }

    /// <summary>
    /// Resolves what a completed eval task cost in tokens, preferring the provider's own accounting.
    /// </summary>
    /// <param name="response">The agent's response, whose usage the model provider populates.</param>
    /// <param name="inputPrompt">The prompt sent, used only by the fallback estimate.</param>
    /// <param name="output">The text produced, used only by the fallback estimate.</param>
    /// <returns>The task's token cost. Never negative.</returns>
    /// <remarks>
    /// <para>
    /// Cost is one of the two things evaluation reports, and it feeds candidate selection: with it pinned
    /// to zero a candidate that solved every task by burning three times the context scored as identically
    /// cheap as one that solved them directly, so the cheaper agent could not win on the axis it wins on
    /// (issue #267).
    /// </para>
    /// <para>
    /// The provider's own figure is preferred because it is what actually gets billed, and it counts the
    /// whole request — the system prompt and every skill body the candidate loaded included, which is
    /// exactly the spending being compared. Providers populate usage inconsistently: some report a total,
    /// while others (the echo client used for offline runs among them) report input and output separately
    /// and leave the total unset, so both shapes are read before giving up.
    /// </para>
    /// <para>
    /// The fallback is the same characters-per-token estimate the context budget uses, over the prompt and
    /// the produced text only. It is a floor, not an equivalent: it cannot see the system prompt or the
    /// skill bodies, so a candidate's real spending is understated on that path. It is kept because a low
    /// figure derived from real text still orders candidates better than a zero that ranks them all equal.
    /// Taking it is logged, so a run whose costs look implausibly uniform can be checked rather than
    /// guessed at.
    /// </para>
    /// </remarks>
    private long ResolveTokenCost(AgentResponse? response, string inputPrompt, string output)
    {
        var billed = response?.Usage.TotalTokens() ?? 0;
        if (billed > 0)
            return billed;

        var estimated = TokenEstimationHelper.EstimateTokens(inputPrompt)
            + TokenEstimationHelper.EstimateTokens(output);

        _logger.LogDebug(
            "Model provider reported no usable token usage; falling back to an estimate of {EstimatedTokens} tokens",
            estimated);

        return estimated;
    }

    private static string ExtractContent(object? response)
    {
        if (response is null)
            return string.Empty;
        if (response is string str)
            return str;
        if (response is AgentResponse agentResponse)
            return agentResponse.Text ?? string.Empty;
        if (response is ChatResponse chatResponse)
        {
            return string.Join("\n", chatResponse.Messages
                .Where(m => m.Role == ChatRole.Assistant)
                .SelectMany(m => m.Contents.OfType<TextContent>())
                .Select(tc => tc.Text));
        }

        return response.GetType().GetProperty("Content")?.GetValue(response)?.ToString()
            ?? response.ToString()
            ?? string.Empty;
    }
}
