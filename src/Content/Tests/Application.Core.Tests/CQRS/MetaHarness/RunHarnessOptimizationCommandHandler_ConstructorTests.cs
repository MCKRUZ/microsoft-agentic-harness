using Application.AI.Common.Interfaces.MetaHarness;
using Application.Core.CQRS.MetaHarness;
using Domain.Common.Config.MetaHarness;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.MetaHarness;

/// <summary>
/// Tests for <see cref="RunHarnessOptimizationCommandHandler"/> constructor null-guard validation.
/// Verifies that all required dependencies are validated with <see cref="ArgumentNullException"/>.
/// </summary>
public class RunHarnessOptimizationCommandHandler_ConstructorTests
{
    private readonly Mock<IHarnessProposer> _proposer = new();
    private readonly Mock<IEvaluationService> _evaluator = new();
    private readonly Mock<IHarnessCandidateRepository> _repository = new();
    private readonly Mock<ISnapshotBuilder> _snapshotBuilder = new();
    private readonly Mock<IRegressionSuiteService> _regressionService = new();
    private readonly Mock<IOptionsMonitor<MetaHarnessConfig>> _configMonitor = new();
    private readonly Mock<ILogger<RunHarnessOptimizationCommandHandler>> _logger = new();

    [Fact]
    public void Constructor_NullProposer_ThrowsArgumentNullException()
    {
        var act = () => new RunHarnessOptimizationCommandHandler(
            null!, _evaluator.Object, _repository.Object,
            _snapshotBuilder.Object, _regressionService.Object,
            _configMonitor.Object, _logger.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("proposer");
    }

    [Fact]
    public void Constructor_NullEvaluationService_ThrowsArgumentNullException()
    {
        var act = () => new RunHarnessOptimizationCommandHandler(
            _proposer.Object, null!, _repository.Object,
            _snapshotBuilder.Object, _regressionService.Object,
            _configMonitor.Object, _logger.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("evaluationService");
    }

    [Fact]
    public void Constructor_NullCandidateRepository_ThrowsArgumentNullException()
    {
        var act = () => new RunHarnessOptimizationCommandHandler(
            _proposer.Object, _evaluator.Object, null!,
            _snapshotBuilder.Object, _regressionService.Object,
            _configMonitor.Object, _logger.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("candidateRepository");
    }

    [Fact]
    public void Constructor_NullSnapshotBuilder_ThrowsArgumentNullException()
    {
        var act = () => new RunHarnessOptimizationCommandHandler(
            _proposer.Object, _evaluator.Object, _repository.Object,
            null!, _regressionService.Object,
            _configMonitor.Object, _logger.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("snapshotBuilder");
    }

    [Fact]
    public void Constructor_NullRegressionService_ThrowsArgumentNullException()
    {
        var act = () => new RunHarnessOptimizationCommandHandler(
            _proposer.Object, _evaluator.Object, _repository.Object,
            _snapshotBuilder.Object, null!,
            _configMonitor.Object, _logger.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("regressionService");
    }

    [Fact]
    public void Constructor_NullConfig_ThrowsArgumentNullException()
    {
        var act = () => new RunHarnessOptimizationCommandHandler(
            _proposer.Object, _evaluator.Object, _repository.Object,
            _snapshotBuilder.Object, _regressionService.Object,
            null!, _logger.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("config");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new RunHarnessOptimizationCommandHandler(
            _proposer.Object, _evaluator.Object, _repository.Object,
            _snapshotBuilder.Object, _regressionService.Object,
            _configMonitor.Object, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    public void Constructor_AllValidDependencies_DoesNotThrow()
    {
        var act = () => new RunHarnessOptimizationCommandHandler(
            _proposer.Object, _evaluator.Object, _repository.Object,
            _snapshotBuilder.Object, _regressionService.Object,
            _configMonitor.Object, _logger.Object);

        act.Should().NotThrow();
    }
}
