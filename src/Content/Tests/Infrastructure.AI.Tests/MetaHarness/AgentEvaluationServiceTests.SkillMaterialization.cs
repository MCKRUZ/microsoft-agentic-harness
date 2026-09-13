using Application.AI.Common.Interfaces;
using Domain.AI.Agents;
using Infrastructure.AI.Tests.Helpers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Tests for #618: <see cref="Infrastructure.AI.MetaHarness.AgentEvaluationService"/>'s candidate
/// skill materialization (<c>MaterializeCandidateSkills</c>) — split from
/// <see cref="AgentEvaluationServiceTests"/> to keep both files under the repo's line-count
/// guideline (caught by CI's grader gate: the combined file grew to 900+ lines through this fix
/// chain's iterative regression tests).
/// </summary>
public partial class AgentEvaluationServiceTests
{
    /// <summary>
    /// A candidate's proposed skill content must actually reach the eval agent, otherwise
    /// skill-only proposals would grade identically to their parent (a silent no-op).
    /// The eval context must therefore carry a MAF <see cref="AgentSkillsProvider"/> sourced
    /// from the candidate's snapshot.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_CandidateWithSkillSnapshots_WiresSkillsProviderIntoEvalContext()
    {
        AgentExecutionContext? capturedContext = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync(new TestableAIAgent("output"));

        // #618: keys are relative to the active agent's OWN skill directory (matching the real
        // production capture path, ActiveConfigSnapshotBuilder.EnumerateSkillFilesAsync, which uses
        // Path.GetRelativePath(skillDirectory, filePath) against that ONE skill's own root) - no
        // skill-name-prefixed subdirectory. The previous fixture's "research-agent/SKILL.md" key
        // never matched what production actually captures.
        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] =
                "---\nname: research-agent\ndescription: Finds and analyzes information.\n---\n# Research Agent\nDo research.\n"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("provider-task", "prompt", pattern: null) };

        await sut.EvaluateAsync(candidate, tasks);

        Assert.NotNull(capturedContext);
        Assert.NotNull(capturedContext.AIContextProviders);
        Assert.Single(capturedContext.AIContextProviders!.OfType<AgentSkillsProvider>());
    }

    /// <summary>
    /// #618: the wiring test above only proves an <see cref="AgentSkillsProvider"/> object exists in
    /// the context - it never proved the underlying materialized directory would actually be
    /// DISCOVERED by the real SDK, which is exactly how this gap went unnoticed. Verified directly
    /// against the pinned Microsoft.Agents.AI 1.13.0 package (see MaterializeCandidateSkills' own
    /// remarks): AgentFileSkillsSource requires a SKILL.md's declared name to ordinal-equal its own
    /// containing directory's leaf name. This asserts that invariant directly on disk, independent of
    /// the SDK's own (log-only, not exception-based) failure signal.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_CandidateWithSkillSnapshots_MaterializesSkillUnderACorrectlyNamedDirectory()
    {
        // Scoped to the one run-root this test's own EvaluateAsync call creates, not the whole shared
        // %TEMP%\harness-eval-skills tree - an unscoped SearchOption.AllDirectories scan there would
        // both be an unbounded-cost walk of every past run's leftovers and risk a false pass if a
        // concurrently-running test (e.g. the wiring test above, which uses the same skill name)
        // leaves a same-named directory behind (caught in review).
        var evalSkillsRoot = Path.Combine(Path.GetTempPath(), "harness-eval-skills");
        var preExistingRunRoots = Directory.Exists(evalSkillsRoot)
            ? Directory.EnumerateDirectories(evalSkillsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        string? capturedSkillDirectory = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((ctx, _) =>
            {
                // Read the materialized directory back off disk rather than reflecting into the SDK's
                // provider internals - what matters is proving a "research-agent"-named directory
                // containing SKILL.md exists somewhere under this test's OWN run root while the agent
                // is still being constructed (before EvaluateAsync's finally block deletes it).
                var thisRunRoot = Directory.Exists(evalSkillsRoot)
                    ? Directory.EnumerateDirectories(evalSkillsRoot)
                        .FirstOrDefault(d => !preExistingRunRoots.Contains(d))
                    : null;
                capturedSkillDirectory = thisRunRoot is null
                    ? null
                    : Directory.EnumerateDirectories(thisRunRoot, "*", SearchOption.AllDirectories)
                        .FirstOrDefault(d => Path.GetFileName(d) == "research-agent" && File.Exists(Path.Combine(d, "SKILL.md")));
            })
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] =
                "---\nname: research-agent\ndescription: Finds and analyzes information.\n---\n# Research Agent\nDo research.\n"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("materialize-task", "prompt", pattern: null) };

        await sut.EvaluateAsync(candidate, tasks);

        Assert.NotNull(capturedSkillDirectory);
    }

    /// <summary>
    /// A candidate with no skill snapshots must not wire an empty skills provider, and must
    /// not leave a materialized temp directory behind.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_EmptySkillSnapshots_DoesNotWireSkillsProvider()
    {
        AgentExecutionContext? capturedContext = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync(new TestableAIAgent("output"));

        var sut = BuildSut();
        var candidate = BuildCandidate(); // empty SkillFileSnapshots
        var tasks = new[] { BuildTask("empty-task", "prompt", pattern: null) };

        await sut.EvaluateAsync(candidate, tasks);

        Assert.NotNull(capturedContext);
        Assert.True(
            capturedContext.AIContextProviders is null
            || !capturedContext.AIContextProviders.OfType<AgentSkillsProvider>().Any());
    }

    /// <summary>
    /// #618: a candidate with skill files but no SKILL.md ANYWHERE in the snapshot (neither a bare
    /// top-level key nor one nested under a skill-name subdirectory) cannot be materialized into a
    /// loadable directory at all. This must fail the task rather than silently degrade to no skills
    /// provider - a silent degrade would score this malformed proposal identically to its unchanged
    /// parent, the same silent-no-op symptom #618 fixes, just reintroduced via a different
    /// malformed-input shape (caught in review).
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_SkillSnapshotsWithNoSkillMdAnywhere_FailsTask()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string> { ["resources/notes.md"] = "some notes" };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("no-skillmd-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("none form a loadable skill", taskResult.FailureReason);
    }

    /// <summary>
    /// #618 (5th CI-caught regression, found by the grader gate): a nested SKILL.md whose declared
    /// name does NOT match its own directory segment is not a genuine sibling skill, and there's no
    /// bare top-level SKILL.md either - so NOTHING in this snapshot is actually loadable. An earlier
    /// version of the loud-fail check only asked "does a file named SKILL.md exist anywhere",
    /// which is true here, so it let this candidate through silently: nothing threw, the file landed
    /// unrecognized at the run root, and the SDK would load zero skills - the exact silent-no-op
    /// #618 exists to prevent, via a shape the guard never checked against the same admission rule
    /// the rest of the method uses.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_OnlyNestedSkillMdWithMismatchedDeclaredName_FailsTask()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["misnamed/SKILL.md"] =
                "---\nname: something-else\ndescription: Declared name doesn't match its directory.\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("mismatched-nested-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("none form a loadable skill", taskResult.FailureReason);
    }

    /// <summary>
    /// A malformed sibling's frontmatter must not fail an unrelated multi-skill snapshot's OTHER,
    /// unaffected entries. Sibling recognition is advisory (best-effort classification of files that
    /// are already correctly shaped, not the bare-rooted skill's own manifest that #618's fix is
    /// actually about) - a nested SKILL.md with invalid YAML can't be verified as a genuine sibling
    /// either way, so it's treated as "not recognized" rather than propagating the parse failure and
    /// taking down the whole task, which would be a NEW failure mode this fix introduces into a path
    /// that previously never read file content at all for a pure multi-skill snapshot (caught in
    /// correctness review).
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_MultiSkillSnapshotWithOneMalformedSiblingFrontmatter_StillMaterializesTheOthers()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["good-skill/SKILL.md"] =
                "---\nname: good-skill\ndescription: Valid.\n---\nbody",
            // Invalid YAML (unterminated flow mapping) - SkillFrontmatter.Load throws for this.
            ["broken-skill/SKILL.md"] = "---\nname: [unterminated\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("malformed-sibling-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.True(taskResult.Passed);
    }

    /// <summary>
    /// #618 (6th CI-caught regression, found by the grader gate): a bare top-level SKILL.md
    /// declaring the same name as a genuine recognized sibling would resolve both groups to the
    /// identical directory, silently merging two distinct skills' files together with no error -
    /// whichever SKILL.md write happened to win (dictionary enumeration order) would silently
    /// determine which skill's identity survived. Must fail the task loudly instead.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_BareSkillNameCollidesWithASiblingSkill_FailsTask()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] =
                "---\nname: research-agent\ndescription: The bare-rooted skill.\n---\nbody",
            ["research-agent/SKILL.md"] =
                "---\nname: research-agent\ndescription: A distinct sibling with the same name.\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("name-collision-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("collides with a genuine sibling skill directory", taskResult.FailureReason);
    }

    /// <summary>
    /// #618 (regression caught by CI review): a snapshot captured from a MULTI-skill root has every
    /// key already prefixed with its own skill's directory name (e.g. "research-agent/SKILL.md") -
    /// that shape already satisfies the pinned SDK's naming convention exactly as captured, and must
    /// be materialized as-is, NOT re-nested under an additional derived subdirectory. An earlier
    /// version of this fix unconditionally assumed every snapshot was the bare top-level SKILL.md
    /// shape and threw for this one instead, breaking a layout that materialized and loaded
    /// correctly before #618's fix ever existed.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_MultiSkillSnapshotWithPrefixedKeys_MaterializesAsIsWithoutRenesting()
    {
        var evalSkillsRoot = Path.Combine(Path.GetTempPath(), "harness-eval-skills");
        var preExistingRunRoots = Directory.Exists(evalSkillsRoot)
            ? Directory.EnumerateDirectories(evalSkillsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        AgentExecutionContext? capturedContext = null;
        // Checked INSIDE the callback, before EvaluateAsync's own finally block deletes the temp
        // directory - the caller never gets the path back, so this is the only window it exists in.
        bool? researchAgentSkillMdExistedAtTheRightPath = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((ctx, _) =>
            {
                capturedContext = ctx;
                var thisRunRoot = Directory.Exists(evalSkillsRoot)
                    ? Directory.EnumerateDirectories(evalSkillsRoot)
                        .FirstOrDefault(d => !preExistingRunRoots.Contains(d))
                    : null;
                // Must land exactly one level under the run root (not re-nested a second time under
                // an additional derived-name subdirectory).
                researchAgentSkillMdExistedAtTheRightPath = thisRunRoot is not null
                    && File.Exists(Path.Combine(thisRunRoot, "research-agent", "SKILL.md"));
            })
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["research-agent/SKILL.md"] =
                "---\nname: research-agent\ndescription: Finds and analyzes information.\n---\nDo research.\n",
            ["other-skill/SKILL.md"] =
                "---\nname: other-skill\ndescription: Does something else.\n---\nDo other things.\n"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("multi-skill-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.True(taskResult.Passed);
        Assert.NotNull(capturedContext);
        Assert.NotNull(capturedContext.AIContextProviders);
        Assert.Single(capturedContext.AIContextProviders!.OfType<AgentSkillsProvider>());
        Assert.True(researchAgentSkillMdExistedAtTheRightPath);
    }

    /// <summary>
    /// #618 (2nd CI-caught regression, hybrid shape): both shapes can coexist in ONE snapshot -
    /// ProposeChangesExecutor.ApplyProposalToSnapshot merges an LLM-authored proposal's keys into the
    /// current snapshot with no shape validation, so a proposal against an already-multi-skill seed
    /// can add a bare top-level SKILL.md alongside pre-existing "research-agent/SKILL.md"-shaped
    /// entries. Classifying the WHOLE snapshot from one key (the previous version of this fix)
    /// mis-routes the already-correct "other-skill" entry into the bare skill's derived-name
    /// subdirectory whenever a bare key is also present. Both groups must land independently and
    /// correctly in the same materialization.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_HybridSnapshotWithBareAndPrefixedKeys_PlacesEachGroupIndependently()
    {
        var evalSkillsRoot = Path.Combine(Path.GetTempPath(), "harness-eval-skills");
        var preExistingRunRoots = Directory.Exists(evalSkillsRoot)
            ? Directory.EnumerateDirectories(evalSkillsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        bool? bareSkillLandedUnderItsDerivedName = null;
        bool? prefixedSkillLandedAtItsOwnUnmodifiedPath = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((_, _) =>
            {
                var thisRunRoot = Directory.Exists(evalSkillsRoot)
                    ? Directory.EnumerateDirectories(evalSkillsRoot)
                        .FirstOrDefault(d => !preExistingRunRoots.Contains(d))
                    : null;
                bareSkillLandedUnderItsDerivedName = thisRunRoot is not null
                    && File.Exists(Path.Combine(thisRunRoot, "bare-skill", "SKILL.md"));
                // Must NOT have been dragged one level deeper under "bare-skill/other-skill/..." -
                // it belongs at the run root, exactly as its own key says.
                prefixedSkillLandedAtItsOwnUnmodifiedPath = thisRunRoot is not null
                    && File.Exists(Path.Combine(thisRunRoot, "other-skill", "SKILL.md"))
                    && !File.Exists(Path.Combine(thisRunRoot, "bare-skill", "other-skill", "SKILL.md"));
            })
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] =
                "---\nname: bare-skill\ndescription: Captured from its own directory directly.\n---\nbody",
            ["other-skill/SKILL.md"] =
                "---\nname: other-skill\ndescription: Already correctly shaped.\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("hybrid-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.True(taskResult.Passed);
        Assert.True(bareSkillLandedUnderItsDerivedName);
        Assert.True(prefixedSkillLandedAtItsOwnUnmodifiedPath);
    }

    /// <summary>
    /// #618 (3rd CI/review-caught regression): a key having a directory segment does NOT by itself
    /// mean it belongs to an already-correct sibling skill. A bare-rooted skill's OWN resource files
    /// (a normal skill-authoring convention - SkillResource.RelativePath documents resources as
    /// relative to "the skill's base directory") also have a directory segment, e.g.
    /// "resources/notes.md" alongside a bare top-level SKILL.md. Classifying by "has a slash" alone
    /// (the previous version of this fix) routes that resource file to the run root as a sibling of
    /// the skill's derived-name subdirectory instead of inside it, severing it from its own skill -
    /// the same silent-degradation failure class this whole PR chain exists to close.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_BareSkillWithItsOwnResourceSubfolder_NestsTheResourceUnderTheSameDerivedName()
    {
        var evalSkillsRoot = Path.Combine(Path.GetTempPath(), "harness-eval-skills");
        var preExistingRunRoots = Directory.Exists(evalSkillsRoot)
            ? Directory.EnumerateDirectories(evalSkillsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        bool? resourceLandedInsideTheSkillDirectory = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((_, _) =>
            {
                var thisRunRoot = Directory.Exists(evalSkillsRoot)
                    ? Directory.EnumerateDirectories(evalSkillsRoot)
                        .FirstOrDefault(d => !preExistingRunRoots.Contains(d))
                    : null;
                resourceLandedInsideTheSkillDirectory = thisRunRoot is not null
                    && File.Exists(Path.Combine(thisRunRoot, "research-agent", "resources", "notes.md"));
            })
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] =
                "---\nname: research-agent\ndescription: Finds and analyzes information.\n---\nDo research.\n",
            ["resources/notes.md"] = "some research notes"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("bare-with-resources-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.True(taskResult.Passed);
        Assert.True(resourceLandedInsideTheSkillDirectory);
    }

    /// <summary>
    /// #618 (4th CI/review-caught regression): mere presence of a "{segment}/SKILL.md" key is a
    /// tautology for that key itself (it always "contains" its own key), so it can't distinguish a
    /// genuine sibling skill from a resource file that merely happens to be named SKILL.md inside
    /// the bare-rooted skill's own subfolder (e.g. a template/reference resource at
    /// "examples/SKILL.md" whose frontmatter declares a DIFFERENT name than "examples", or no name
    /// at all). The previous version of this fix treated key-presence alone as sufficient and
    /// misrouted this resource - and everything else sharing its "examples/" segment - to the run
    /// root instead of nesting it inside the bare skill's own directory.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_NestedFileNamedSkillMdWithNonMatchingDeclaredName_IsTreatedAsAResourceNotASibling()
    {
        var evalSkillsRoot = Path.Combine(Path.GetTempPath(), "harness-eval-skills");
        var preExistingRunRoots = Directory.Exists(evalSkillsRoot)
            ? Directory.EnumerateDirectories(evalSkillsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        bool? templateResourceLandedInsideTheSkillDirectory = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExecutionContext, CancellationToken>((_, _) =>
            {
                var thisRunRoot = Directory.Exists(evalSkillsRoot)
                    ? Directory.EnumerateDirectories(evalSkillsRoot)
                        .FirstOrDefault(d => !preExistingRunRoots.Contains(d))
                    : null;
                templateResourceLandedInsideTheSkillDirectory = thisRunRoot is not null
                    && File.Exists(Path.Combine(thisRunRoot, "my-skill", "examples", "SKILL.md"))
                    && !File.Exists(Path.Combine(thisRunRoot, "examples", "SKILL.md"));
            })
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] =
                "---\nname: my-skill\ndescription: Has a template resource shaped like a manifest.\n---\nbody",
            // A template/reference resource that happens to be named SKILL.md but is NOT a real
            // sibling skill - its declared name doesn't match its own directory segment "examples".
            ["examples/SKILL.md"] =
                "---\nname: not-a-real-sibling\ndescription: Template content, not a loadable skill.\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("false-sibling-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.True(taskResult.Passed);
        Assert.True(templateResourceLandedInsideTheSkillDirectory);
    }

    /// <summary>
    /// #618: a SKILL.md with no 'name' in its frontmatter has nothing to derive the required
    /// correctly-named subdirectory from. This must fail the task rather than silently degrade to no
    /// skills provider, for the same reason as the missing-SKILL.md case above.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_SkillMdWithNoDeclaredName_FailsTask()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] = "---\ndescription: Missing a name field.\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("no-name-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("declares no 'name'", taskResult.FailureReason);
    }

    /// <summary>
    /// Snapshot keys come from untrusted LLM proposals, so a path-traversal key must be rejected
    /// (graded as a failed task) and must never write outside the eval temp root.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_SkillSnapshotWithPathTraversalKey_FailsTaskAndDoesNotEscape()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        // #618: a valid top-level SKILL.md is required to reach the per-file materialization loop at
        // all (see MaterializeCandidateSkills' own remarks) - a traversal key with no valid SKILL.md
        // entry would be rejected earlier, for a different reason, proving nothing about THIS
        // protection. The traversal attempt lives in a second, non-SKILL.md entry instead.
        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] = "---\nname: evil\ndescription: escape attempt.\n---\nbody",
            ["../escaped/resource.md"] = "malicious resource content"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("traversal-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("resolves outside", taskResult.FailureReason);
    }

    /// <summary>
    /// #618: the skill NAME itself is untrusted, candidate-authored input with exactly the same
    /// traversal risk as any snapshot file key - SafeResolveWithinRoot is reused for it rather than a
    /// new, unverified sanitizer, so this proves that reuse actually rejects a malicious name.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_SkillSnapshotWithPathTraversalName_FailsTaskAndDoesNotEscape()
    {
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var skillFiles = new Dictionary<string, string>
        {
            ["SKILL.md"] = "---\nname: \"../../escaped\"\ndescription: escape attempt via name.\n---\nbody"
        };
        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("name-traversal-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        var taskResult = Assert.Single(result.PerExampleResults);
        Assert.False(taskResult.Passed);
        Assert.Contains("resolves outside", taskResult.FailureReason);
    }
}
