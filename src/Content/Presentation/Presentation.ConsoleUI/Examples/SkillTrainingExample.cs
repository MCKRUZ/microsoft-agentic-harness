using Application.AI.Common.CQRS.SkillTraining.TrainSkill;
using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.ClaimVerification;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// End-to-end demo of the skill-training subsystem (SkillOpt port). Wires deterministic
/// stubs for <see cref="IRolloutRunner"/> and <see cref="IPatchProposer"/> so the loop runs
/// without engaging real LLM endpoints — useful for verifying the orchestrator state machine
/// and as a starting point for integrating real proposer + rollout runner implementations.
/// </summary>
/// <remarks>
/// What the demo shows:
/// <list type="bullet">
/// <item>The orchestrator runs N epochs × M steps and applies the standard rollout → reflect → aggregate → select → apply → gate pipeline.</item>
/// <item>The deterministic stubs are tuned so the first two steps Accept (each lifting hard from 0.2 → 0.5 → 0.8) and remaining steps Reject.</item>
/// <item>Patience-based early stop terminates the run once consecutive rejects hit the threshold.</item>
/// </list>
/// </remarks>
public sealed class SkillTrainingExample
{
    /// <summary>Runs the demo end-to-end. Prints the per-step audit trail and the run result.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ConsoleHelper.DisplayHeader("Skill Training Demo (SkillOpt port)", Color.Cyan1);
        ConsoleHelper.DisplayModeInfo(isLive: false, "Deterministic stubs — no LLM calls.");

        var handler = BuildHandler();

        var cmd = new TrainSkillCommand
        {
            RunId = "demo-run-1",
            SkillId = "demo-skill",
            InitialSkill = "# Demo Skill\n\n## Approach\n- Start simple.",
            Config = new TrainSkillConfig
            {
                Epochs = 2,
                StepsPerEpoch = 3,
                LrStart = 4,
                LrMin = 1,
                LrScheduler = "cosine",
                GateMetric = GateMetric.Hard,
                Patience = 3,
                UseSlowUpdate = false,
                UseMetaSkill = false,
                Seed = 42
            }
        };

        var result = await handler.Handle(cmd, cancellationToken);
        if (!result.IsSuccess)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Training failed:[/] {string.Join("; ", result.Errors)}");
            return;
        }

        var run = result.Value!;
        var table = new Table().AddColumns("Step", "Epoch", "Action", "Score", "Proposed", "Applied");
        foreach (var s in run.Steps)
        {
            table.AddRow(
                s.Step.ToString(),
                s.Epoch.ToString(),
                s.Action.ToString(),
                s.CandidateScore.ToString("F2"),
                s.ProposedEditCount.ToString(),
                s.AppliedEditCount.ToString());
        }
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLineInterpolated(
            $"[bold]Best:[/] step {run.BestStep}, score {run.BestScore:F2}, accepted any: {run.HasAcceptedAny}");
        AnsiConsole.MarkupLineInterpolated(
            $"[grey]Steps executed: {run.StepsExecuted}, consecutive rejects on exit: {run.ConsecutiveRejects}[/]");
    }

    private static TrainSkillCommandHandler BuildHandler()
    {
        var aggregator = new PatchAggregator();
        var selector = new TopKEditSelector();
        var applier = new PatchApplier();
        var gate = new GateEvaluator();
        var fence = new HarnessPatchValidator(new EditableSurfaceRegistry());
        var suggester = new NoHarnessChangeSuggester();
        var configSurfaceConstraint = new ConfigSurfaceConstraint();
        var suggestionValidator = new HarnessChangeSuggestionValidator(configSurfaceConstraint);
        var store = new InMemorySkillTrainingCheckpointStore();
        var runner = new DeterministicRolloutRunner();
        var proposer = new DeterministicProposer();
        ILogger<TrainSkillCommandHandler> logger = NullLogger<TrainSkillCommandHandler>.Instance;

        // Bare service provider — this demo wires everything else by hand, but
        // ClaimVerificationRunner needs an IServiceProvider for its keyed-reader lookup and an
        // IOptionsMonitor<AppConfig> for its parallelism/timeout config; a one-line container is
        // simpler and less error-prone than hand-rolling either. NoHarnessChangeSuggester never
        // actually produces a suggestion, so this runner is never invoked at runtime in this demo —
        // it exists here only so the handler's dependency graph is genuinely satisfiable.
        var services = new ServiceCollection();
        services.Configure<AppConfig>(_ => { });
        var provider = services.BuildServiceProvider();
        var claimVerificationRunner = new ClaimVerificationRunner(
            new RuleBasedClaimConsequenceClassifier(),
            new NotConfiguredClaimVerifier(NullLogger<NotConfiguredClaimVerifier>.Instance),
            provider,
            provider.GetRequiredService<IOptionsMonitor<AppConfig>>(),
            NullLogger<ClaimVerificationRunner>.Instance);

        return new TrainSkillCommandHandler(
            runner, proposer, aggregator, selector, applier, gate, fence,
            suggester, suggestionValidator, configSurfaceConstraint, claimVerificationRunner, store,
            new NoOpMediator(), TimeProvider.System, logger);
    }

    /// <summary>
    /// Deterministic rollout runner — val scores climb step by step then plateau, mimicking a real
    /// training run where early edits unlock easy wins and later edits face diminishing returns.
    /// </summary>
    private sealed class DeterministicRolloutRunner : IRolloutRunner
    {
        private int _valCalls;
        public Task<IReadOnlyList<RolloutResult>> RunAsync(string skill, RolloutBatch batch, CancellationToken ct)
        {
            // Train rollouts always fail so the optimizer always has something to propose.
            if (batch.Split == "train")
            {
                return Task.FromResult<IReadOnlyList<RolloutResult>>(
                    [new RolloutResult { ItemId = "t1", Hard = 0.0, Soft = 0.2 }]);
            }

            // Val rollouts: synthesize an increasing-then-flat curve.
            _valCalls++;
            var hard = _valCalls switch
            {
                1 => 0.5,
                2 => 0.8,
                _ => 0.5    // stalls — gate will reject from here on
            };
            return Task.FromResult<IReadOnlyList<RolloutResult>>(
                [new RolloutResult { ItemId = "v1", Hard = hard, Soft = hard }]);
        }
    }

    /// <summary>
    /// Deterministic patch proposer — appends a different rule per call so each candidate
    /// differs from the current skill and the applier reports HasChanges.
    /// </summary>
    private sealed class DeterministicProposer : IPatchProposer
    {
        private int _calls;
        public Task<Patch> ProposeAsync(ReflectionInput input, CancellationToken cancellationToken)
        {
            _calls++;
            return Task.FromResult(new Patch
            {
                Edits = [new Edit { Op = EditOp.Append, Content = $"- rule #{_calls}" }],
                Reasoning = $"demo proposal {_calls}"
            });
        }
    }

    private sealed class NoOpMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
            => Task.FromResult(default(TResponse)!);
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => Task.CompletedTask;
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification
            => Task.CompletedTask;
    }
}
