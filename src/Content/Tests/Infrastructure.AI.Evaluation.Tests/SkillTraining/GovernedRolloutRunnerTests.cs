using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Models.Conversations;
using Domain.AI.Evaluation;
using Domain.AI.SkillTraining;
using Domain.Common.Config.AI.SkillTraining;
using FluentAssertions;
using Infrastructure.AI.Evaluation.SkillTraining;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.SkillTraining;

/// <summary>
/// Pins <see cref="GovernedRolloutRunner"/> (#843): every rollout is an agent turn with the candidate
/// skill as its system prompt, scored by the case's own metrics; whatever would make a rollout measure
/// nothing is refused before the first turn is paid for.
/// </summary>
public sealed class GovernedRolloutRunnerTests
{
    private const string Candidate = "# candidate skill\n- be careful\n";

    private static EvalCase Case(string id, string split = "val", IReadOnlyDictionary<string, string>? overrides = null, string metric = "exact_match") => new()
    {
        Id = id,
        Input = "task " + id,
        ExpectedOutput = "ANSWER-" + id,
        Tags = [$"split:{split}", "arithmetic"],
        InvocationOverrides = overrides ?? new Dictionary<string, string>(),
        MetricSpecs = metric.Length == 0 ? [] : [new MetricSpec { MetricKey = metric }]
    };

    private static SkillTrainingConfig Config(int concurrency = 2) => new()
    {
        DatasetPath = "dataset.yaml",
        RolloutAgentName = "worker",
        MaxRolloutConcurrency = concurrency
    };

    private static GovernedRolloutRunner Sut(
        FakeInvoker invoker,
        FakeLoader loader,
        SkillTrainingConfig? config = null,
        params IEvalMetric[] metrics) => new(
            invoker,
            metrics.Length > 0 ? metrics : [new ExactMatchByAnswerMetric()],
            [loader],
            new StaticMonitor(config ?? Config()),
            NullLogger<GovernedRolloutRunner>.Instance);

    private static RolloutBatch Batch(int size = 10, string split = "val") => new() { Split = split, BatchSize = size, Seed = 1 };

    [Fact]
    public async Task Run_EachRolloutIsAnAgentTurnWithTheCandidateAsItsSystemPrompt()
    {
        var invoker = new FakeInvoker();
        var sut = Sut(invoker, new FakeLoader(Case("a"), Case("b")));

        await sut.RunAsync(Candidate, Batch(), CancellationToken.None);

        invoker.Calls.Should().HaveCount(2);
        invoker.Calls.Should().OnlyContain(c =>
            c.RunLevel!["system_prompt"] == Candidate && c.RunLevel["agent_name"] == "worker" && !c.Deterministic);
    }

    [Fact]
    public async Task Run_ScoresEachItemByItsOwnMetricsAndKeepsTheOrderOfThePinnedBatch()
    {
        var invoker = new FakeInvoker { Answer = id => id == "b" ? "ANSWER-b" : "wrong" };
        var sut = Sut(invoker, new FakeLoader(Case("a"), Case("b"), Case("c")));

        var results = await sut.RunAsync(Candidate, Batch() with { ItemIds = ["c", "b", "a"] }, CancellationToken.None);

        results.Select(r => r.ItemId).Should().Equal("c", "b", "a");
        results.Select(r => r.Hard).Should().Equal(0.0, 1.0, 0.0);
        results.Single(r => r.ItemId == "b").Soft.Should().Be(1.0);
    }

    [Fact]
    public async Task Run_TheTrajectoryNeverCarriesTheAnswerKey_AndTheTaskTypeIsTheFirstNonSplitTag()
    {
        var invoker = new FakeInvoker { Answer = _ => "my attempt" };
        var sut = Sut(invoker, new FakeLoader(Case("a")));

        var result = (await sut.RunAsync(Candidate, Batch(), CancellationToken.None)).Single();

        result.Trajectory.Should().Contain("task a").And.Contain("my attempt").And.NotContain("ANSWER-a");
        result.TaskType.Should().Be("arithmetic");
    }

    [Theory]
    [InlineData("system_prompt")]
    [InlineData("agent_name")]
    [InlineData("target")]
    [InlineData("SYSTEM_PROMPT")]
    public async Task Run_ACaseThatOverridesWhatTheCandidateReplaces_IsRefusedBeforeAnyTurn(string key)
    {
        var invoker = new FakeInvoker();
        var sut = Sut(invoker, new FakeLoader(Case("ok"), Case("bad", overrides: new Dictionary<string, string> { [key] = "x" })));

        var act = () => sut.RunAsync(Candidate, Batch(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("skill_training.rollout_case_overrides_candidate*");
        invoker.Calls.Should().BeEmpty("a misconfiguration must cost no tokens");
    }

    [Fact]
    public async Task Run_ACaseOverridingOnlyTemperature_IsFine()
    {
        var sut = Sut(new FakeInvoker(), new FakeLoader(Case("a", overrides: new Dictionary<string, string> { ["temperature"] = "0.2" })));

        var results = await sut.RunAsync(Candidate, Batch(), CancellationToken.None);

        results.Should().ContainSingle();
    }

    [Fact]
    public async Task Run_ACaseWithNoMetrics_IsRefusedBeforeAnyTurn()
    {
        var invoker = new FakeInvoker();
        var sut = Sut(invoker, new FakeLoader(Case("a", metric: "")));

        var act = () => sut.RunAsync(Candidate, Batch(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("skill_training.rollout_case_unscorable*");
        invoker.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_AMetricNothingIsRegisteredUnder_IsRefusedBeforeAnyTurn()
    {
        var invoker = new FakeInvoker();
        var sut = Sut(invoker, new FakeLoader(Case("a", metric: "no_such_metric")));

        var act = () => sut.RunAsync(Candidate, Batch(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("skill_training.rollout_metric_unknown*");
        invoker.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "worker")]
    [InlineData("dataset.yaml", "")]
    public async Task Run_WithoutADatasetAndAnAgent_SaysHowToConfigureIt(string dataset, string agent)
    {
        var config = Config();
        config.DatasetPath = dataset;
        config.RolloutAgentName = agent;
        var sut = Sut(new FakeInvoker(), new FakeLoader(Case("a")), config);

        var act = () => sut.RunAsync(Candidate, Batch(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("skill_training.rollout_not_configured*");
    }

    [Fact]
    public async Task Run_AnInvocationThatThrows_IsAFailedRolloutNotAFailedBatch()
    {
        var invoker = new FakeInvoker { Throw = id => id == "a" };
        var sut = Sut(invoker, new FakeLoader(Case("a"), Case("b")));

        var results = await sut.RunAsync(Candidate, Batch() with { ItemIds = ["a", "b"] }, CancellationToken.None);

        results[0].Hard.Should().Be(0.0);
        results[0].Trajectory.Should().Contain("THE RUN DID NOT FINISH").And.NotContain("boom");
        results[1].Hard.Should().Be(1.0, "the other rollout is unaffected");
    }

    [Fact]
    public async Task Run_Cancellation_IsNotSwallowedAsAFailedRollout()
    {
        using var cts = new CancellationTokenSource();
        var invoker = new FakeInvoker { Cancel = true };
        var sut = Sut(invoker, new FakeLoader(Case("a")));

        var act = () => sut.RunAsync(Candidate, Batch(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Run_AMetricThatThrows_DoesNotPass()
    {
        var sut = Sut(new FakeInvoker(), new FakeLoader(Case("a", metric: "boom")), metrics: [new ThrowingMetric()]);

        var result = (await sut.RunAsync(Candidate, Batch(), CancellationToken.None)).Single();

        result.Hard.Should().Be(0.0);
        result.Soft.Should().Be(0.0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Run_NeverRunsMoreRolloutsAtOnceThanConfigured(int limit)
    {
        var invoker = new FakeInvoker { Delay = TimeSpan.FromMilliseconds(40) };
        var sut = Sut(invoker, new FakeLoader(Enumerable.Range(0, 8).Select(i => Case($"c{i}")).ToArray()), Config(limit));

        await sut.RunAsync(Candidate, Batch(8), CancellationToken.None);

        invoker.MaxConcurrent.Should().Be(limit, "the limit is reached and never exceeded");
    }

    [Fact]
    public async Task Run_ReadsTheDatasetOncePerRunner()
    {
        var loader = new FakeLoader(Case("a"));
        var sut = Sut(new FakeInvoker(), loader);

        await sut.RunAsync(Candidate, Batch(), CancellationToken.None);
        await sut.RunAsync(Candidate, Batch(), CancellationToken.None);

        loader.Loads.Should().Be(1, "one training run sees one stable item pool");
    }

    [Fact]
    public async Task Run_ADatasetThatCannotBeRead_SaysSoWithoutQuotingThePathOrTheError()
    {
        var loader = new FakeLoader(Case("a")) { Fail = new InvalidDataException("bad yaml at C:\\secret\\path") };
        var sut = Sut(new FakeInvoker(), loader);

        var act = () => sut.RunAsync(Candidate, Batch(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().StartWith("skill_training.rollout_dataset_unreadable").And.NotContain("secret");
    }

    [Fact]
    public async Task Run_ADatasetFormatNoLoaderHandles_IsRefused()
    {
        var config = Config();
        config.DatasetPath = "dataset.toml";
        var sut = Sut(new FakeInvoker(), new FakeLoader(Case("a")), config);

        var act = () => sut.RunAsync(Candidate, Batch(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("skill_training.rollout_dataset_format*");
    }

    [Fact]
    public async Task Run_ARunThatCarriesToolCalls_ShowsThemInTheTrajectory()
    {
        var invoker = new FakeInvoker { Tools = [new ToolCallRecord("lookup", "{\"id\":1}", "{\"ok\":true}", 3)] };
        var sut = Sut(invoker, new FakeLoader(Case("a")));

        var result = (await sut.RunAsync(Candidate, Batch(), CancellationToken.None)).Single();

        result.Trajectory.Should().Contain("Step 1: lookup").And.Contain("{\"ok\":true}");
    }

    private sealed class StaticMonitor(SkillTrainingConfig value) : IOptionsMonitor<SkillTrainingConfig>
    {
        public SkillTrainingConfig CurrentValue => value;
        public SkillTrainingConfig Get(string? name) => value;
        public IDisposable? OnChange(Action<SkillTrainingConfig, string?> listener) => null;
    }

    private sealed class FakeLoader(params EvalCase[] cases) : IEvalDatasetLoader
    {
        public IReadOnlyList<string> Extensions { get; } = ["yaml", "yml"];
        public int Loads { get; private set; }
        public Exception? Fail { get; init; }

        public Task<EvalDataset> LoadAsync(string path, CancellationToken cancellationToken)
        {
            Loads++;
            if (Fail is not null)
                throw Fail;
            return Task.FromResult(new EvalDataset { Name = "d", Cases = cases });
        }
    }

    private sealed class FakeInvoker : IAgentInvoker
    {
        private int _running;
        private int _max;

        public List<(EvalCase Case, IReadOnlyDictionary<string, string>? RunLevel, bool Deterministic)> Calls { get; } = [];
        public Func<string, string> Answer { get; init; } = id => "ANSWER-" + id;
        public Func<string, bool> Throw { get; init; } = _ => false;
        public bool Cancel { get; init; }
        public TimeSpan Delay { get; init; }
        public IReadOnlyList<ToolCallRecord> Tools { get; init; } = [];
        public int MaxConcurrent => _max;

        public async Task<AgentInvocationResult> InvokeAsync(
            EvalCase @case, IReadOnlyDictionary<string, string>? runLevelOverrides, bool forceDeterministic, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add((@case, runLevelOverrides, forceDeterministic));

            var now = Interlocked.Increment(ref _running);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            try
            {
                if (Delay > TimeSpan.Zero)
                    await Task.Delay(Delay, cancellationToken);
                if (Cancel)
                    throw new OperationCanceledException();
                if (Throw(@case.Id))
                    throw new InvalidOperationException("boom");

                return new AgentInvocationResult { Success = true, Output = Answer(@case.Id), ToolCalls = Tools };
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    /// <summary>Passes when the output is exactly the case's expected output.</summary>
    private sealed class ExactMatchByAnswerMetric : IEvalMetric
    {
        public string Key => "exact_match";

        public Task<MetricScore> ScoreAsync(EvalCase @case, AgentInvocationResult output, MetricSpec spec, CancellationToken cancellationToken)
        {
            var match = output.Output == @case.ExpectedOutput;
            return Task.FromResult(new MetricScore
            {
                MetricKey = Key,
                Score = match ? 1.0 : 0.0,
                Verdict = match ? Verdict.Pass : Verdict.Fail
            });
        }
    }

    private sealed class ThrowingMetric : IEvalMetric
    {
        public string Key => "boom";

        public Task<MetricScore> ScoreAsync(EvalCase @case, AgentInvocationResult output, MetricSpec spec, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("metric blew up");
    }
}
