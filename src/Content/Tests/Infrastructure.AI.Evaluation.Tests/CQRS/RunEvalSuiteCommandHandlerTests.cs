using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.Core.CQRS.Evaluation.RunEvalSuite;
using Domain.AI.Evaluation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Domain.Common.Config;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.CQRS;

public sealed class RunEvalSuiteCommandHandlerTests : IDisposable
{
    private readonly string _tempDir;

    public RunEvalSuiteCommandHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eval-handler-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string CreateFile(string name, string content = "stub")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static EvalDataset MakeDataset(string name = "d") => new()
    {
        Name = name,
        Cases = [new EvalCase
        {
            Id = "c1",
            Input = "i",
            MetricSpecs = [new MetricSpec { MetricKey = "exact_match" }]
        }]
    };

    private static EvalRunReport MakeReport(string runId = "run-1") => new()
    {
        RunId = runId,
        StartedAtUtc = DateTimeOffset.UtcNow,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromMilliseconds(1),
        Datasets = [MakeDataset()],
        Results = [],
        OverallVerdict = Verdict.Pass
    };

    private static Mock<IEvalDatasetLoader> Loader(string ext, Func<string, EvalDataset>? loadFunc = null)
        => Loader(new[] { ext }, loadFunc);

    private static Mock<IEvalDatasetLoader> Loader(IReadOnlyList<string> exts, Func<string, EvalDataset>? loadFunc = null)
    {
        var mock = new Mock<IEvalDatasetLoader>();
        mock.SetupGet(l => l.Extensions).Returns(exts);
        mock.Setup(l => l.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string p, CancellationToken _) =>
                loadFunc is not null ? loadFunc(p) : MakeDataset(Path.GetFileNameWithoutExtension(p)));
        return mock;
    }

    /// <summary>
    /// Builds the handler with a real, UNCONFINED path guard — the shipped default, which is what the
    /// EvalRunner CLI runs under. A stub guard would make these tests blind to the handler actually
    /// consulting it; confinement itself is covered directly in <c>EvalDatasetPathGuardTests</c>.
    /// </summary>
    private static RunEvalSuiteCommandHandler MakeSut(
        IEnumerable<IEvalDatasetLoader> loaders,
        IEvalRunner runner,
        AppConfig? config = null) => new(
            loaders,
            runner,
            new EvalDatasetPathGuard(MonitorFor(config ?? new AppConfig()), new EvalConfinementLatch(false)),
            MonitorFor(config ?? new AppConfig()),
            NullLogger<RunEvalSuiteCommandHandler>.Instance);

    private static IOptionsMonitor<AppConfig> MonitorFor(AppConfig config)
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(config);
        return monitor.Object;
    }

    [Fact]
    public async Task Refuses_a_run_whose_case_count_exceeds_the_configured_ceiling()
    {
        // Every case is a governed agent turn plus its LLM-judge calls, multiplied by Repeats. This is
        // the last point at which the spend can be refused instead of incurred, so the check has to
        // happen BEFORE the runner is handed anything - asserting the runner was never invoked is the
        // part that matters; a test that only checked the returned failure would pass even if the run
        // had already been paid for.
        var config = new AppConfig();
        config.AI.Evaluation.MaxCaseExecutionsPerRun = 1;

        var path1 = CreateFile("a.yaml");
        var path2 = CreateFile("b.yaml");
        var loader = Loader([".yaml"]);
        var runner = new Mock<IEvalRunner>();

        var sut = MakeSut([loader.Object], runner.Object, config);

        var result = await sut.Handle(
            new RunEvalSuiteCommand { DatasetPaths = [path1, path2] }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        runner.Verify(
            r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the ceiling must refuse the run before any model spend");
    }

    [Fact]
    public async Task Allows_a_run_at_exactly_the_configured_ceiling()
    {
        // Boundary: the ceiling is inclusive. An off-by-one here would refuse a legitimate suite that
        // an operator had sized deliberately.
        var config = new AppConfig();
        config.AI.Evaluation.MaxCaseExecutionsPerRun = 2;

        var path1 = CreateFile("a.yaml");
        var path2 = CreateFile("b.yaml");
        var loader = Loader([".yaml"]);
        var runner = new Mock<IEvalRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeReport());

        var sut = MakeSut([loader.Object], runner.Object, config);

        var result = await sut.Handle(
            new RunEvalSuiteCommand { DatasetPaths = [path1, path2] }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Refuses_an_oversized_dataset_without_parsing_it()
    {
        // The execution ceiling can only be applied once cases exist, and producing cases means parsing.
        // So the size check has to come first, and the loader must never be called — otherwise the parse
        // cost of an arbitrarily large file is paid in full before anything is refused.
        var config = new AppConfig();
        config.AI.Evaluation.MaxDatasetBytes = 32;

        var path = CreateFile("big.yaml", new string('x', 1024));
        var loader = Loader([".yaml"]);
        var runner = new Mock<IEvalRunner>();

        var sut = MakeSut([loader.Object], runner.Object, config);

        var result = await sut.Handle(
            new RunEvalSuiteCommand { DatasetPaths = [path] }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        loader.Verify(
            l => l.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the size cap exists to avoid the parse, so refusing after parsing would defeat it");
    }

    [Fact]
    public async Task Stops_loading_as_soon_as_the_ceiling_is_exceeded()
    {
        // Summing at the end would parse every named file before deciding. Bailing on the dataset that
        // crosses the ceiling is what keeps the refusal cheaper than the run.
        var config = new AppConfig();
        config.AI.Evaluation.MaxCaseExecutionsPerRun = 1;

        var path1 = CreateFile("a.yaml");
        var path2 = CreateFile("b.yaml");
        var path3 = CreateFile("c.yaml");
        var loader = Loader([".yaml"]);
        var runner = new Mock<IEvalRunner>();

        var sut = MakeSut([loader.Object], runner.Object, config);

        var result = await sut.Handle(
            new RunEvalSuiteCommand { DatasetPaths = [path1, path2, path3] }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();

        // One case each: the first is at the ceiling, the second crosses it, the third is never reached.
        loader.Verify(
            l => l.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "loading must stop at the dataset that crosses the ceiling, not continue through the list");
    }

    [Fact]
    public async Task Counts_repeats_against_the_ceiling_rather_than_cases_alone()
    {
        // The ceiling exists to bound spend, and Repeats multiplies spend: each one re-invokes every
        // case and its judge. A ceiling counting cases alone would cap nothing, because the same two
        // cases could be asked for fifty times over and still read as "2".
        var config = new AppConfig();
        config.AI.Evaluation.MaxCaseExecutionsPerRun = 5;

        var path1 = CreateFile("a.yaml");
        var path2 = CreateFile("b.yaml");
        var loader = Loader([".yaml"]);
        var runner = new Mock<IEvalRunner>();

        var sut = MakeSut([loader.Object], runner.Object, config);

        // Two cases — comfortably under 5. Four repeats makes it eight executions, which is not.
        var result = await sut.Handle(
            new RunEvalSuiteCommand
            {
                DatasetPaths = [path1, path2],
                Options = new EvalRunOptions { Repeats = 4 },
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        runner.Verify(
            r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "repeats must be priced in before the run is handed to the runner");
    }

    [Fact]
    public async Task Loads_each_dataset_via_extension_match_and_passes_to_runner()
    {
        var path1 = CreateFile("a.yaml");
        var path2 = CreateFile("b.yaml");

        IReadOnlyList<EvalDataset>? capturedDatasets = null;
        var loader = Loader("yaml");
        var runner = new Mock<IEvalRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()))
              .Callback<IReadOnlyList<EvalDataset>, EvalRunOptions, CancellationToken>((d, _, _) => capturedDatasets = d)
              .ReturnsAsync(MakeReport());

        var sut = MakeSut([loader.Object], runner.Object);

        var result = await sut.Handle(new RunEvalSuiteCommand
        {
            DatasetPaths = [path1, path2],
            Options = new EvalRunOptions()
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        capturedDatasets.Should().NotBeNull().And.HaveCount(2);
        loader.Verify(l => l.LoadAsync(path1, It.IsAny<CancellationToken>()), Times.Once);
        loader.Verify(l => l.LoadAsync(path2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Extension_matching_is_case_insensitive_and_handles_leading_dot()
    {
        var path = CreateFile("a.YAML");
        var loader = Loader("yaml");
        var runner = new Mock<IEvalRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeReport());

        var sut = MakeSut([loader.Object], runner.Object);

        var result = await sut.Handle(new RunEvalSuiteCommand { DatasetPaths = [path] }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Loader_registered_for_multiple_extensions_handles_each_spelling()
    {
        var ymlPath = CreateFile("a.yml");
        var yamlPath = CreateFile("b.yaml");

        var loader = Loader(new[] { "yaml", "yml" });
        var runner = new Mock<IEvalRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeReport());

        var sut = MakeSut([loader.Object], runner.Object);

        var result = await sut.Handle(new RunEvalSuiteCommand { DatasetPaths = [ymlPath, yamlPath] }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        loader.Verify(l => l.LoadAsync(ymlPath, It.IsAny<CancellationToken>()), Times.Once);
        loader.Verify(l => l.LoadAsync(yamlPath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Attaches_cost_warning_to_report_when_repeats_exceeds_threshold()
    {
        var path = CreateFile("a.yaml");
        var loader = Loader("yaml");
        var runner = new Mock<IEvalRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeReport());

        var sut = MakeSut([loader.Object], runner.Object);

        var result = await sut.Handle(
            new RunEvalSuiteCommand
            {
                DatasetPaths = [path],
                Options = new EvalRunOptions { Repeats = 25 }
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Warnings.Should().ContainSingle()
            .Which.Should().Contain("Repeats=25");
    }

    /// <summary>
    /// Regression test, found by /code-review on the #437 PR: every prior test's <c>MakeReport()</c>
    /// left <c>Warnings</c> empty, so none of them exercised the branch where the handler's own
    /// collected warnings (e.g. the repeats-cost warning) AND the runner's own emitted warnings
    /// (e.g. #437's recognized-key validation) are both non-empty at once. A regression that
    /// reordered the merge, reverted to overwriting instead of appending, or accidentally
    /// deduplicated would have passed every other test silently.
    /// </summary>
    [Fact]
    public async Task Preserves_both_handler_and_runner_warnings_when_both_are_present()
    {
        var path = CreateFile("a.yaml");
        var loader = Loader("yaml");
        var runner = new Mock<IEvalRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeReport() with { Warnings = ["runner-emitted warning"] });

        var sut = MakeSut([loader.Object], runner.Object);

        var result = await sut.Handle(
            new RunEvalSuiteCommand
            {
                DatasetPaths = [path],
                Options = new EvalRunOptions { Repeats = 25 }
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Warnings.Should().HaveCount(2);
        result.Value!.Warnings.Should().Contain(w => w.Contains("Repeats=25"));
        result.Value!.Warnings.Should().Contain("runner-emitted warning");
    }

    [Fact]
    public async Task Does_not_attach_warning_when_repeats_at_or_below_threshold()
    {
        var path = CreateFile("a.yaml");
        var loader = Loader("yaml");
        var runner = new Mock<IEvalRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeReport());

        var sut = MakeSut([loader.Object], runner.Object);

        var result = await sut.Handle(
            new RunEvalSuiteCommand
            {
                DatasetPaths = [path],
                Options = new EvalRunOptions { Repeats = 10 }
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_validation_failure_when_dataset_paths_empty()
    {
        var loader = Loader("yaml");
        var runner = new Mock<IEvalRunner>(MockBehavior.Strict);

        var sut = MakeSut([loader.Object], runner.Object);

        var result = await sut.Handle(new RunEvalSuiteCommand { DatasetPaths = [] }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(Domain.Common.ResultFailureType.Validation);
        runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Returns_not_found_when_dataset_file_missing()
    {
        var missing = Path.Combine(_tempDir, "missing.yaml");
        var runner = new Mock<IEvalRunner>(MockBehavior.Strict);
        var loader = Loader("yaml");

        var sut = MakeSut([loader.Object], runner.Object);

        var result = await sut.Handle(new RunEvalSuiteCommand { DatasetPaths = [missing] }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(Domain.Common.ResultFailureType.NotFound);
        result.Errors.Should().Contain(e => e.Contains("missing.yaml"));
        runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Returns_failure_when_no_loader_registered_for_extension()
    {
        var path = CreateFile("a.toml");
        var loader = Loader("yaml");
        var runner = new Mock<IEvalRunner>(MockBehavior.Strict);

        var sut = MakeSut([loader.Object], runner.Object);

        var result = await sut.Handle(new RunEvalSuiteCommand { DatasetPaths = [path] }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("toml"));
        runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Returns_failure_when_loader_throws_invalid_data()
    {
        var path = CreateFile("a.yaml");
        var loader = new Mock<IEvalDatasetLoader>();
        loader.SetupGet(l => l.Extensions).Returns(new[] { "yaml" });
        loader.Setup(l => l.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidDataException("bad yaml"));

        var runner = new Mock<IEvalRunner>(MockBehavior.Strict);
        var sut = MakeSut([loader.Object], runner.Object);

        var result = await sut.Handle(new RunEvalSuiteCommand { DatasetPaths = [path] }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("bad yaml"));
        runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Forwards_options_to_runner()
    {
        var path = CreateFile("a.yaml");
        var loader = Loader("yaml");

        EvalRunOptions? capturedOptions = null;
        var runner = new Mock<IEvalRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()))
              .Callback<IReadOnlyList<EvalDataset>, EvalRunOptions, CancellationToken>((_, o, _) => capturedOptions = o)
              .ReturnsAsync(MakeReport());

        var options = new EvalRunOptions { Repeats = 3, Parallelism = 4, FailRateThreshold = 0.1, ForceDeterministic = true };

        var sut = MakeSut([loader.Object], runner.Object);
        await sut.Handle(new RunEvalSuiteCommand { DatasetPaths = [path], Options = options }, CancellationToken.None);

        capturedOptions.Should().BeSameAs(options);
    }

    [Fact]
    public async Task Returns_report_from_runner_on_success()
    {
        var path = CreateFile("a.yaml");
        var loader = Loader("yaml");
        var report = MakeReport("expected-run-id");

        var runner = new Mock<IEvalRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<IReadOnlyList<EvalDataset>>(), It.IsAny<EvalRunOptions>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(report);

        var sut = MakeSut([loader.Object], runner.Object);
        var result = await sut.Handle(new RunEvalSuiteCommand { DatasetPaths = [path] }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(report);
    }
}
