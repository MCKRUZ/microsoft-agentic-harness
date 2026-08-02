using System.Diagnostics;
using Application.Common.Logging;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Common.Tests.Logging;

public sealed class StructuredJsonLoggerProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StructuredJsonLoggerProvider _provider;

    public StructuredJsonLoggerProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jsonlogger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = Mock.Of<IOptionsMonitor<LoggingConfig>>(m =>
            m.CurrentValue == new LoggingConfig { LogsBasePath = _tempDir });
        _provider = new StructuredJsonLoggerProvider(config);
    }

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void IsRunActive_BeforeStart_ReturnsFalse()
    {
        _provider.IsRunActive.Should().BeFalse();
    }

    [Fact]
    public void CompleteRun_UnderLoad_PersistsEveryEntryAndDoesNotStall()
    {
        // The drain thread dequeues an entry, then takes _lock to write it. CompleteRun used to Join
        // that thread WHILE holding _lock, so whenever an entry was in flight the two deadlocked until
        // the 2-second Join timeout expired — after which the writer was disposed and that entry was
        // silently dropped. Both symptoms are asserted: nothing lost, and no multi-second stall.
        //
        // Repeated because the interleaving is probabilistic; a single run reproduced it only about
        // one time in three.
        const int iterations = 12;
        const int entriesPerRun = 50;

        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            var runId = $"drain-race-{i}";
            _provider.StartNewRun(runId);

            var logger = _provider.CreateLogger("DrainRace");
            for (var n = 0; n < entriesPerRun; n++)
                logger.LogInformation("entry {Index}", n);

            _provider.CompleteRun();

            var path = Path.Combine(_tempDir, runId, "structured.jsonl");
            File.ReadAllLines(path).Should().HaveCount(
                entriesPerRun, "run {0} must persist every entry it accepted", runId);
        }

        stopwatch.Stop();

        // Each stalled CompleteRun costs the full 2s Join timeout. Allow generous headroom for slow
        // CI while still failing decisively if the deadlock is reintroduced.
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(iterations),
            "CompleteRun must not block on the drain thread it is holding the lock against");
    }

    [Fact]
    public void StartNewRun_SetsIsRunActiveToTrue()
    {
        _provider.StartNewRun("json-run-1");

        _provider.IsRunActive.Should().BeTrue();
    }

    [Fact]
    public void StartNewRun_CreatesRunDirectory()
    {
        _provider.StartNewRun("json-dir-test");

        Directory.Exists(Path.Combine(_tempDir, "json-dir-test")).Should().BeTrue();
    }

    [Fact]
    public void StartNewRun_PathTraversal_ThrowsArgumentException()
    {
        var act = () => _provider.StartNewRun("../evil");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*escapes*base*");
    }

    [Fact]
    public void Log_WritesJsonlEntry()
    {
        _provider.StartNewRun("json-write-test");
        var logger = _provider.CreateLogger("TestCategory");

        logger.LogInformation("Test JSON message");

        _provider.CompleteRun();

        var jsonlPath = Path.Combine(_tempDir, "json-write-test", "structured.jsonl");
        File.Exists(jsonlPath).Should().BeTrue();

        var content = File.ReadAllText(jsonlPath);
        content.Should().Contain("Test JSON message");
        content.Should().Contain("\"level\":\"info\"");
        content.Should().Contain("\"category\":\"TestCategory\"");
    }

    [Fact]
    public void Log_WhenRunNotActive_DoesNotThrow()
    {
        var logger = _provider.CreateLogger("TestCategory");

        var act = () => logger.LogInformation("No run active");

        act.Should().NotThrow();
    }

    [Fact]
    public void Log_IsEnabled_ReturnsFalseWhenRunNotActive()
    {
        var logger = _provider.CreateLogger("TestCategory");

        logger.IsEnabled(LogLevel.Information).Should().BeFalse();
    }

    [Fact]
    public void CompleteRun_SetsIsRunActiveToFalse()
    {
        _provider.StartNewRun("json-complete-test");

        _provider.CompleteRun();

        _provider.IsRunActive.Should().BeFalse();
    }

    [Fact]
    public void CreateLogger_ReturnsSameInstanceForSameCategory()
    {
        var logger1 = _provider.CreateLogger("Cat");
        var logger2 = _provider.CreateLogger("Cat");

        logger1.Should().BeSameAs(logger2);
    }

    [Fact]
    public void StartNewRun_WithPhase_CreatesPhaseSubdirectory()
    {
        _provider.StartNewRun("json-phase-test", "analysis");

        Directory.Exists(Path.Combine(_tempDir, "json-phase-test", "analysis"))
            .Should().BeTrue();
    }

    [Fact]
    public void Log_WithException_IncludesExceptionInJsonOutput()
    {
        _provider.StartNewRun("json-exception-test");
        var logger = _provider.CreateLogger("TestCategory");
        var ex = new InvalidOperationException("JSON exception");

        logger.LogError(ex, "Error in JSON");

        _provider.CompleteRun();

        var content = File.ReadAllText(
            Path.Combine(_tempDir, "json-exception-test", "structured.jsonl"));
        content.Should().Contain("JSON exception");
        content.Should().Contain("\"exception\":");
    }

    [Fact]
    public void StartNewRun_EmptyBasePath_DoesNotActivateRun()
    {
        var config = Mock.Of<IOptionsMonitor<LoggingConfig>>(m =>
            m.CurrentValue == new LoggingConfig { LogsBasePath = "" });
        using var provider = new StructuredJsonLoggerProvider(config);

        provider.StartNewRun("run-1");

        provider.IsRunActive.Should().BeFalse();
    }
}
