using System.Text.RegularExpressions;
using Application.AI.Common.Services.SkillTraining;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.SkillTraining;
using Domain.Common.Config.AI.SkillTraining;
using FluentAssertions;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.SkillTraining;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.SkillTraining;

/// <summary>
/// Pins <see cref="AgenticPatchProposer"/> (#843) against a scripted agent session: what it writes for the
/// session to read, what it asks, and that whatever the session returns reaches the loop only as bounded
/// edits that reproduce the returned document exactly, or as an empty patch.
/// </summary>
public sealed partial class AgenticPatchProposerTests : IDisposable
{
    private static readonly PatchApplier Applier = new();
    private static readonly HarnessPatchValidator Fence = new(new EditableSurfaceRegistry());
    private const string Current = "# skill\n- rule\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agentic-proposer-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [GeneratedRegex(@"(?<dir>\S+?)/skill/SKILL\.md")]
    private static partial Regex SkillPath();

    private AgenticPatchProposer NewSut(ScriptedSession session, SkillTrainingConfig? config = null) => new(
        session,
        new OwnerOnlyDirectoryCreator(),
        new SkillTrainingEvidenceRoot(_root),
        new StaticMonitor(config ?? new SkillTrainingConfig()),
        Applier,
        Fence,
        NullLogger<AgenticPatchProposer>.Instance);

    private static ReflectionInput Input(params ProposerRolloutView[] rollouts) => new()
    {
        CurrentSkill = Current,
        Rollouts = rollouts.Length > 0 ? rollouts : [new ProposerRolloutView("case-1", false, "the agent gave up", string.Empty)],
    };

    private static string Doc(string skill) => $"{ProposerReply.BeginSkill}\n{skill}\n{ProposerReply.EndSkill}";

    private static string Reply(string skill, string notes = "run 1: gave up") =>
        $"{Doc(skill)}\n{ProposerReply.BeginNotes}\n{notes}\n{ProposerReply.EndNotes}";

    [Fact]
    public async Task Propose_ADocumentThatImprovesTheSkill_ComesBackAsEditsThatReproduceItExactly()
    {
        var session = new ScriptedSession(Reply("# skill\n- rule\n- check the total first\n"));

        var patch = await NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        patch.Edits.Should().NotBeEmpty();
        patch.Reasoning.Should().Be("run 1: gave up");
        Applier.Apply(Current, patch).NewSkillContent.Should().Be("# skill\n- rule\n- check the total first\n");
    }

    [Fact]
    public async Task Propose_KeepBase_IsAnEmptyPatchNotAFailure()
    {
        var session = new ScriptedSession($"{ProposerReply.BeginNotes}\nKEEP_BASE: nothing here argues for a change\n{ProposerReply.EndNotes}");

        var patch = await NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        patch.Edits.Should().BeEmpty();
        patch.Reasoning.Should().Contain("KEEP_BASE");
        session.Calls.Should().Be(1, "KEEP_BASE is a real answer and is not retried");
    }

    [Theory]
    [InlineData("# skill\n- rule\n")]
    [InlineData("# skill\n- rule")]
    [InlineData("# skill\r\n- rule\r\n")]
    public async Task Propose_TheUnchangedDocument_IsAnEmptyPatchEvenWhenOnlyTheTrailingNewlineDiffers(string returned)
    {
        var session = new ScriptedSession(Doc(returned));

        var patch = await NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        patch.Edits.Should().BeEmpty();
    }

    [Fact]
    public async Task Propose_AReplyInTheWrongFormat_IsSentBackOnceWithTheReasonAndThenAccepted()
    {
        var session = new ScriptedSession("I think the skill is fine.", Reply("# skill\n- rule\n- second rule\n"));

        var patch = await NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        session.Calls.Should().Be(2);
        session.Missions[1].Should().Contain("YOUR PREVIOUS ATTEMPT WAS REJECTED").And.Contain($"no {ProposerReply.BeginSkill} block");
        patch.Edits.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Propose_TwoUnusableReplies_ReturnAnEmptyPatchRatherThanGuessing()
    {
        var session = new ScriptedSession("no format", Doc(string.Empty));

        var patch = await NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        patch.Edits.Should().BeEmpty();
        session.Calls.Should().Be(2, "one retry, no more");
    }

    [Fact]
    public async Task Propose_ASessionThatFails_ThrowsWithoutQuotingItsError()
    {
        var session = new ScriptedSession { Failure = "401 for https://x/?sig=SECRET-SAS-TOKEN" };

        var act = () => NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().NotContain("SECRET");
    }

    [Fact]
    public async Task Propose_EvidenceCarryingAVerdict_AsksNothingAndProposesNothing()
    {
        var session = new ScriptedSession(Reply("# skill\n- changed\n"));
        var input = Input(new ProposerRolloutView("case-1", false, "{\"OverallVerdict\":\"Fail\"}", string.Empty));

        var patch = await NewSut(session).ProposeAsync(input, CancellationToken.None);

        session.Calls.Should().Be(0, "evidence with a verdict in it must not reach the model");
        patch.Edits.Should().BeEmpty();
    }

    [Fact]
    public async Task Propose_TheSessionIsAskedAsTheConfiguredAgentAboutTheEvidenceFolder()
    {
        var session = new ScriptedSession(Reply("# skill\n- rule\n- x\n"));
        var config = new SkillTrainingConfig { ProposerAgentName = "my-optimizer", MaxProposerTurns = 7, WorkDescription = "Reconcile vendor invoices." };
        var input = Input(
            new ProposerRolloutView("case-1", false, "gave up", string.Empty),
            new ProposerRolloutView("case-2", true, "worked", string.Empty));

        await NewSut(session, config).ProposeAsync(input, CancellationToken.None);

        var command = session.Commands.Single();
        command.AgentName.Should().Be("my-optimizer");
        command.MaxTurns.Should().Be(7);
        var mission = command.UserMessages.Single();
        mission.Should().Contain("Reconcile vendor invoices.").And.Contain("2 run(s): 1 passed, 1 did not");
        mission.Should().Contain("/skill/SKILL.md").And.Contain("/rollouts/");
        mission.ToLowerInvariant().Should().NotContain("score");
    }

    [Fact]
    public async Task Propose_TheEvidenceExistsForTheSessionAndIsGoneAfterwards()
    {
        string? folderDuringSession = null;
        var skillAtDispatch = string.Empty;
        var session = new ScriptedSession(Reply("# skill\n- rule\n- x\n"))
        {
            OnSend = mission =>
            {
                var match = SkillPath().Match(mission);
                folderDuringSession = match.Groups["dir"].Value;
                skillAtDispatch = File.ReadAllText(Path.Combine(folderDuringSession, "skill", "SKILL.md"));
            }
        };

        await NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        skillAtDispatch.Should().Be(Current, "the session reads the real files");
        Directory.Exists(folderDuringSession).Should().BeFalse("trajectories are not kept after the step");
    }

    [Theory]
    [InlineData("KEEP_BASE")]
    [InlineData("KEEP_BASE: the runs do not argue for a change.")]
    public async Task Propose_ABareKeepBase_IsAnAnswerNotAMalformedReply(string reply)
    {
        var session = new ScriptedSession(reply);

        var patch = await NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        patch.Edits.Should().BeEmpty();
        session.Calls.Should().Be(1, "a one-word answer is an answer; asking again would only invite a worse one");
    }

    [Fact]
    public async Task Propose_ADocumentThatContainsTagsOfItsOwn_ComesBackWholeNotCutAtTheFirstOne()
    {
        var document = "# skill\n- rule\n\nReply like this:\n\n<skill>\n...\n</skill>\n\n- last rule\n";
        var session = new ScriptedSession(Reply(document));

        var patch = await NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        Applier.Apply(Current, patch).NewSkillContent.Should().Be(document);
    }

    [Fact]
    public async Task Propose_AnAmbiguousDocument_IsSentBackOnceAndThenLeftAlone()
    {
        var ambiguous = Doc("# skill\n===END SKILL===\nmore");
        var session = new ScriptedSession(ambiguous, ambiguous);

        var patch = await NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        patch.Edits.Should().BeEmpty();
        session.Calls.Should().Be(2);
        session.Missions[1].Should().Contain("cannot be told");
    }

    [Fact]
    public async Task Propose_ASkillSavedWithWindowsLineEndings_ComesBackWithThemAndChangesOnlyWhatChanged()
    {
        var current = "# skill\r\n- rule\r\n- other\r\n";
        var input = new ReflectionInput
        {
            CurrentSkill = current,
            Rollouts = [new ProposerRolloutView("case-1", false, "gave up", string.Empty)],
        };
        var session = new ScriptedSession(Reply("# skill\n- rule\n- other\n- third\n"));

        var patch = await NewSut(session).ProposeAsync(input, CancellationToken.None);

        Applier.Apply(current, patch).NewSkillContent.Should().Be("# skill\r\n- rule\r\n- other\r\n- third\r\n");
        patch.Edits.Should().ContainSingle("one added line is one edit, not a rewrite of every line");
    }

    [Theory]
    [InlineData("# skill\n- rule\n\n\n")]
    [InlineData("# skill\n- rule\n  \n")]
    [InlineData("# skill\n- rule")]
    public async Task Propose_TheCurrentSkillsTrailingWhitespace_IsKeptVerbatim(string current)
    {
        var input = new ReflectionInput
        {
            CurrentSkill = current,
            Rollouts = [new ProposerRolloutView("case-1", false, "gave up", string.Empty)],
        };
        var unchanged = new ScriptedSession(Reply("# skill\n- rule"));
        var changed = new ScriptedSession(Reply("# skill\n- rule\n- more"));

        var none = await NewSut(unchanged).ProposeAsync(input, CancellationToken.None);
        var some = await NewSut(changed).ProposeAsync(input, CancellationToken.None);

        none.Edits.Should().BeEmpty("the same words with the same ending are no change");
        Applier.Apply(current, some).NewSkillContent.Should().EndWith("- more" + current[current.TrimEnd().Length..]);
    }

    [Fact]
    public async Task Propose_TheSessionCanReadOnlyItsOwnFolder_AndOnlyWhileItRuns()
    {
        string? published = null;
        string? folder = null;
        var session = new ScriptedSession(Reply("# skill\n- rule\n- x\n"))
        {
            OnSend = mission =>
            {
                folder = SkillPath().Match(mission).Groups["dir"].Value;
                published = SkillTrainingEvidenceScope.Current;
            }
        };

        await NewSut(session).ProposeAsync(Input(), CancellationToken.None);

        published.Should().Be(Path.GetFullPath(folder!), "the evidence tool serves this step's folder and no other");
        SkillTrainingEvidenceScope.Current.Should().BeNull("nothing stays readable after the step");
    }

    [Fact]
    public async Task Propose_OneLeakingRollout_IsLeftOutAndTheRestStillGoToTheSession()
    {
        var filesTheSessionSaw = new List<string>();
        var session = new ScriptedSession(Reply("# skill\n- rule\n- x\n"))
        {
            OnSend = mission => filesTheSessionSaw.AddRange(
                Directory.GetFiles(Path.Combine(SkillPath().Match(mission).Groups["dir"].Value, "rollouts"))
                    .Select(f => Path.GetFileName(f)))
        };
        var input = Input(
            new ProposerRolloutView("hostile", false, "{\"OverallVerdict\":\"Pass\"}", string.Empty),
            new ProposerRolloutView("honest", false, "the agent gave up", string.Empty));

        var patch = await NewSut(session).ProposeAsync(input, CancellationToken.None);

        session.Calls.Should().Be(1, "one hostile trajectory must not be a switch that stops the step");
        filesTheSessionSaw.Should().ContainSingle().Which.Should().Contain("honest");
        session.Missions[0].Should().Contain("1 run(s)");
        patch.Edits.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Propose_AVerdictInTheSkillItself_AsksNothing()
    {
        var session = new ScriptedSession(Reply("# skill\n- changed\n"));
        var input = new ReflectionInput
        {
            CurrentSkill = "{\"OverallVerdict\":\"Fail\"}",
            Rollouts = [new ProposerRolloutView("case-1", false, "gave up", string.Empty)],
        };

        var patch = await NewSut(session).ProposeAsync(input, CancellationToken.None);

        session.Calls.Should().Be(0);
        patch.Edits.Should().BeEmpty();
    }

    [Fact]
    public void Verify_AccurateEditsInsideTheFence_AreAccepted()
    {
        var patch = new Patch { Edits = SkillDocumentDiff.Between(Current, "# skill\n- rule\n- more\n") };

        AgenticPatchProposer.Verify(Applier, Fence, Current, "# skill\n- rule\n- more\n", patch).Should().BeNull();
    }

    [Fact]
    public void Verify_EditsThatDoNotReproduceTheDocument_AreRefused()
    {
        var wrong = new Patch { Edits = [new Edit { Op = EditOp.Append, Content = "- something else" }] };

        AgenticPatchProposer.Verify(Applier, Fence, Current, "# skill\n- rule\n- more\n", wrong)
            .Should().Contain("reproduce it exactly");
    }

    [Fact]
    public void Verify_AnEditOnAFrozenSurface_IsRefusedByTheFence()
    {
        var onFrozenSurface = new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "- more", Surface = HarnessSurface.SystemPrompt }]
        };
        var proposed = Applier.Apply(Current, onFrozenSurface).NewSkillContent;

        AgenticPatchProposer.Verify(Applier, Fence, Current, proposed, onFrozenSurface)
            .Should().Contain("may not edit");
    }

    private sealed class StaticMonitor(SkillTrainingConfig value) : IOptionsMonitor<SkillTrainingConfig>
    {
        public SkillTrainingConfig CurrentValue => value;
        public SkillTrainingConfig Get(string? name) => value;
        public IDisposable? OnChange(Action<SkillTrainingConfig, string?> listener) => null;
    }

    /// <summary>An agent session that answers each dispatch with the next scripted reply.</summary>
    private sealed class ScriptedSession(params string[] replies) : IMediator
    {
        private readonly Queue<string> _replies = new(replies);

        public int Calls { get; private set; }
        public List<RunConversationCommand> Commands { get; } = [];
        public IReadOnlyList<string> Missions => [.. Commands.Select(c => c.UserMessages.Single())];
        public string? Failure { get; init; }
        public Action<string>? OnSend { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var command = (RunConversationCommand)(object)request;
            Calls++;
            Commands.Add(command);
            OnSend?.Invoke(command.UserMessages.Single());

            var result = Failure is not null
                ? new ConversationResult { Success = false, Turns = [], FinalResponse = string.Empty, Error = Failure }
                : new ConversationResult { Success = true, Turns = [], FinalResponse = _replies.Count > 0 ? _replies.Dequeue() : string.Empty };
            return Task.FromResult((TResponse)(object)result);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
