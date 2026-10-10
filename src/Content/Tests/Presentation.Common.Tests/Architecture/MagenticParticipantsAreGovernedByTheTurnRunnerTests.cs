using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Architecture guard: a Magentic participant is authorized as itself only when the turn runner wraps it
/// with <c>ParticipantGovernance</c> (#769). Source-scans the production tree and fails when a new file
/// starts driving <c>IMagenticOrchestrator</c> directly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a test.</strong> The wrapper is applied at one call site, in
/// <c>MagenticAgentTurnRunner</c>. <c>IMagenticOrchestrator.RunAsync</c> takes participants as bare agents,
/// so a second caller — a bundle workflow, a new endpoint — runs them under whatever pipeline happens to be
/// ambient, or ungoverned when none is (<c>GovernedAIFunction</c> lets a call through when nothing is
/// published). Nothing at the compiler or the type level distinguishes that caller from the governed one.
/// This is the "control nothing guarantees is invoked" shape this repo has shipped before.
/// </para>
/// <para>
/// Modelled on <see cref="AttributionIsPublishedByTheExecutionContextOnlyTests"/>.
/// </para>
/// </remarks>
public sealed class MagenticParticipantsAreGovernedByTheTurnRunnerTests
{
    private const string Member = "IMagenticOrchestrator";

    private const string TheRunner = "Application/Application.Core/Orchestration/Magentic/MagenticAgentTurnRunner.cs";

    /// <summary>
    /// Files permitted to name the orchestrator, each with the reason it is not an ungoverned caller. Add
    /// here — with a reason — rather than deleting the check. Matched on the full relative path.
    /// </summary>
    private static readonly (string RelativePath, string Reason)[] Exemptions =
    [
        ("Application/Application.AI.Common/Interfaces/Orchestration/Magentic/IMagenticOrchestrator.cs",
            "Declares it."),
        ("Application/Application.AI.Common/Interfaces/Orchestration/Magentic/MagenticWorkflowRequest.cs",
            "Documents the request type; calls nothing."),
        ("Infrastructure/Infrastructure.AI/Orchestration/Magentic/MagenticOrchestrator.cs",
            "Implements it."),
        ("Infrastructure/Infrastructure.AI/DependencyInjection.cs", "Registration."),
        ("Infrastructure/Infrastructure.AI/DependencyInjection.Magentic.cs", "Registration."),
        ("Application/Application.Core/DependencyInjection.cs", "Registration of the turn runner."),
        ("Application/Application.Core/Orchestration/Magentic/IMagenticAgentTurnRunner.cs",
            "Documents the runner's contract; calls nothing."),
        ("Domain/Domain.AI/Agents/AgentDefinition.cs", "Documentation reference only."),
        (TheRunner, "THE governed caller: wraps every participant with ParticipantGovernance."),
        ("Presentation/Presentation.ConsoleUI/Examples/MagenticOrchestrationExample.cs",
            "A console demonstration with no admission pipeline published at all, so its manager is "
            + "ungoverned too; wrapping only its participants would add a half-governed state. Not a "
            + "production path."),
    ];

    [Fact]
    public void OnlyTheListedFilesNameTheOrchestrator()
    {
        var violations = ReadProductionSources()
            .Where(s => s.Code.Contains(Member, StringComparison.Ordinal))
            .Select(s => s.Path.Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !Exemptions.Any(e => path.EndsWith("/" + e.RelativePath, StringComparison.Ordinal)))
            .ToList();

        if (violations.Count > 0)
        {
            Assert.Fail(
                $"{Member} was named outside the files allowed to drive it:" + Environment.NewLine +
                string.Join(Environment.NewLine, violations.Select(v => "  " + v)) +
                Environment.NewLine + Environment.NewLine +
                "A Magentic participant is authorized as itself only if it is wrapped with " +
                "ParticipantGovernance before the orchestrator runs it (#769). The orchestrator takes bare " +
                "agents, so a caller that submits them unwrapped runs every participant's tool calls under " +
                "whatever pipeline is ambient — or ungoverned when none is. Wrap the participants the way " +
                "MagenticAgentTurnRunner does, or add this file to Exemptions with the reason it is safe.");
        }
    }

    [Fact]
    public void TheTurnRunner_StillWrapsEveryParticipant()
    {
        // The other direction: a guard that only forbids other callers passes happily when the wrap is
        // deleted from the one caller it protects.
        var runner = ReadProductionSources()
            .Where(s => s.Path.Replace(Path.DirectorySeparatorChar, '/')
                .EndsWith("/" + TheRunner, StringComparison.Ordinal))
            .ToList();

        runner.Should().ContainSingle($"{TheRunner} is the governed caller; if it moved, update TheRunner and Exemptions");
        runner[0].Code.Should().Contain(
            "governanceTurn.Wrap(",
            "without this call every participant is authorized as the entry agent again (#769)");
        runner[0].Code.Should().Contain(
            "_participantGovernance.ForTurn(",
            "the turn the participants are wrapped through is what folds their traces in and releases their scopes (#804)");
    }

    [Fact]
    public void EveryExemptedFile_StillExists()
    {
        var paths = ReadProductionSources()
            .Select(s => s.Path.Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        Exemptions
            .Where(e => !paths.Any(p => p.EndsWith("/" + e.RelativePath, StringComparison.Ordinal)))
            .Select(e => e.RelativePath)
            .Should().BeEmpty("an exemption for a file that no longer exists is a permission waiting to be inherited");
    }

    private static IReadOnlyList<(string Path, string Code)> ReadProductionSources()
    {
        var sources = SourceScan.ReadProductionSources(Path.Combine(RepoRoot.Path, "src", "Content"));

        sources.Should().HaveCountGreaterThan(2000,
            "the scan must cover the production tree; a much smaller scan means the root lookup or the "
            + "filter broke, and every assertion built on it is then meaningless");

        return sources;
    }
}
