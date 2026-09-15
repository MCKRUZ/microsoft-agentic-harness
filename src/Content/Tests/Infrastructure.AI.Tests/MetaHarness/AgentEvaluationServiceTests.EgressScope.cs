using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Skills;
using Domain.AI.Agents;
using Domain.Common.Config.MetaHarness;
using Infrastructure.AI.MetaHarness;
using Infrastructure.AI.Skills;
using Infrastructure.AI.Tests.Helpers;
using Infrastructure.AI.Tests.Planner.StepExecutors;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that keeps what was written — used sparingly, only where
/// a decision has no other observable effect. #618 /code-review finding: when
/// <c>DisclosableSkillFactory.Create</c> silently drops a candidate skill, the resulting fallback to
/// the harness-wide default egress policy is otherwise indistinguishable from "no candidate skill at
/// all" from outside this class, so the warning log IS the behavior under test.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public bool Logged(LogLevel level, string fragment) =>
        _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue((logLevel, formatter(state, exception)));
}

/// <summary>
/// Tests for #618's remaining gap: an eval candidate's own <c>egress:</c> allowlist must actually
/// reach <see cref="Infrastructure.AI.Egress.SkillManifestEgressPolicyResolver"/> during the agent run, not just get parsed.
/// Split from <see cref="AgentEvaluationServiceTests"/> and its SkillMaterialization partial per
/// this project's Partial Class Pattern.
/// </summary>
public partial class AgentEvaluationServiceTests
{
    private static readonly Dictionary<string, string> SkillWithEgressAllowlist = new()
    {
        ["SKILL.md"] =
            "---\n" +
            "name: research-agent\n" +
            "description: Finds and analyzes information.\n" +
            "egress:\n" +
            "  allowlist:\n" +
            "    - host: api.example.com\n" +
            "      schemes: [https]\n" +
            "      ports: [443]\n" +
            "---\n" +
            "# Research Agent\nDo research.\n"
    };

    /// <summary>
    /// The ephemeral scope must be open for exactly the span of <c>agent.RunAsync</c> — proved by
    /// observing <see cref="EphemeralSkillMetadataAccessor.TryGet"/> from INSIDE the fake agent's own
    /// run handler, the same async flow the real <c>GovernedAIFunction</c> would consult it from.
    /// Before #618, nothing published anything here — this fails on main without the fix.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_CandidateWithEgressAllowlist_PublishesEphemeralSkillDuringRunAsync()
    {
        Domain.AI.Skills.SkillDefinition? observedDuringRun = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent((_, _) =>
            {
                observedDuringRun = EphemeralSkillMetadataAccessor.TryGet("research-agent");
                return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "output")));
            }));

        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: SkillWithEgressAllowlist);
        var tasks = new[] { BuildTask("egress-task", "prompt", pattern: null) };

        await sut.EvaluateAsync(candidate, tasks);

        Assert.NotNull(observedDuringRun);
        Assert.Equal("research-agent", observedDuringRun!.Id);
        Assert.Equal("api.example.com", observedDuringRun.Egress?.Allowlist.Single().Host);

        // Teardown is NOT re-checked here (correctness-review finding): AsyncLocal writes made
        // inside RunCandidateTurnAsync's nested async call never flow back to this test method's
        // own context once EvaluateAsync returns, so a post-await TryGet here would pass whether
        // or not the `using` actually disposed — it proves nothing. Teardown/isolation between two
        // scopes is meaningfully proven at the resolver level instead, where the assertion runs on
        // the SAME async flow as the `using` blocks: see
        // SkillManifestEgressPolicyResolverTests.ResolveFor_TwoEphemeralSkillsSharingAnId_EachResolvesItsOwnAllowlist.
    }

    /// <summary>
    /// #618 scoped this fix to the common single-skill case (see
    /// <see cref="EphemeralSkillMetadataAccessor"/>'s remarks). A candidate with NO skill files at
    /// all must not attempt to build a candidate skill definition — proving the null-skillDirectory
    /// path composes cleanly with the new wiring rather than throwing.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_CandidateWithNoSkillFiles_RunsWithoutEphemeralScope()
    {
        Domain.AI.Skills.SkillDefinition? observedDuringRun = null;
        var sawEphemeralLookup = false;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent((_, _) =>
            {
                sawEphemeralLookup = true;
                observedDuringRun = EphemeralSkillMetadataAccessor.TryGet("anything");
                return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "output")));
            }));

        var sut = BuildSut();
        var candidate = BuildCandidate(); // empty SkillFileSnapshots
        var tasks = new[] { BuildTask("no-skill-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        Assert.True(sawEphemeralLookup);
        Assert.Null(observedDuringRun);
        Assert.Equal(1.0, result.PassRate);
    }

    /// <summary>
    /// #618 /code-review finding: a candidate whose SKILL.md is frontmatter-only (no body text) parses
    /// into a perfectly valid <c>SkillDefinition</c> — the harness-level parser has no non-empty-body
    /// check — but <c>DisclosableSkillFactory.Create</c> silently drops any skill with no
    /// <c>Instructions</c>, since there is nothing for <c>load_skill</c> to serve. Before this fix,
    /// <c>GoverningToolContextProvider</c> still got built with an empty <c>disclosableSkills</c> list
    /// and no signal that anything was wrong — recreating the exact silent-under-scoping failure #618
    /// was filed to close, for a different, entirely realistic input shape. This proves the fix: a
    /// clear warning is logged, and the eval still completes (fails closed to the default policy, not
    /// closed to a crash).
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_CandidateWithFrontmatterOnlySkill_LogsWarningInsteadOfSilentlyDroppingScope()
    {
        var skillFiles = new Dictionary<string, string>
        {
            // No body after the closing "---": SkillMetadataParser.Build happily produces a
            // SkillDefinition with Instructions == "", but AgentInlineSkill's own constructor
            // (via DisclosableSkillFactory.Create) refuses to register a skill with nothing for
            // load_skill to serve.
            ["SKILL.md"] = "---\nname: research-agent\ndescription: Finds and analyzes information.\n---\n"
        };
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("output"));

        var capturingLogger = new CapturingLogger<AgentEvaluationService>();
        var cfg = new MetaHarnessConfig { TraceDirectoryRoot = _traceRoot };
        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == cfg);
        var sut = new AgentEvaluationService(
            opts, BuildTraceStore(cfg.TraceDirectoryRoot), _agentFactoryMock.Object,
            PermissiveAdmission.Pipeline(), PermissiveAdmission.PermissiveSanitizer(),
            new CurrentSkillAccessor(), Mock.Of<IMcpSecurityScanner>(MockBehavior.Strict),
            DisabledScanningConfig(), new EgressManifestValidator(),
            NullLoggerFactory.Instance, capturingLogger);

        var candidate = BuildCandidate(skillFiles: skillFiles);
        var tasks = new[] { BuildTask("frontmatter-only-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        Assert.True(
            capturingLogger.Logged(LogLevel.Warning, "research-agent"),
            "expected a warning naming the dropped candidate skill, not a silent fallback");
        Assert.Equal(1.0, result.PassRate);
    }
}
