using Application.Common.Logging;
using Domain.Common.Config;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Application.Common.Tests.Logging;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLoggerProvider _provider;

    public FileLoggerProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"filelogger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = Mock.Of<IOptionsMonitor<LoggingConfig>>(m =>
            m.CurrentValue == new LoggingConfig { LogsBasePath = _tempDir });
        _provider = new FileLoggerProvider(config, new PlainDirectoryCreator());
    }

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        // The container disposes the provider once per registration that resolved it: the ILoggerProvider one and the
        // concrete one forwarding to it. A second pass used to throw on the disposed queue at host shutdown.
        _provider.StartNewRun("run-dispose");

        _provider.Dispose();
        var second = () => _provider.Dispose();

        second.Should().NotThrow();
    }

    [Fact]
    public void IsRunActive_BeforeStartNewRun_ReturnsFalse()
    {
        _provider.IsRunActive.Should().BeFalse();
    }

    [Fact]
    public void StartNewRun_SetsIsRunActiveToTrue()
    {
        _provider.StartNewRun("run-1");

        _provider.IsRunActive.Should().BeTrue();
    }

    [Fact]
    public void StartNewRun_CreatesRunDirectory()
    {
        _provider.StartNewRun("run-dir-test");

        var runDir = Path.Combine(_tempDir, "run-dir-test");
        Directory.Exists(runDir).Should().BeTrue();
    }

    [Fact]
    public void StartNewRun_WithPhase_CreatesPhaseSubdirectory()
    {
        _provider.StartNewRun("run-phase-test", "planning");

        var phaseDir = Path.Combine(_tempDir, "run-phase-test", "planning");
        Directory.Exists(phaseDir).Should().BeTrue();
    }

    [Theory]
    [InlineData("../evil-run", null)]
    [InlineData("../logsX", null)]
    [InlineData("nested/run", null)]
    [InlineData("..", null)]
    [InlineData("run", "../../logsX")]
    [InlineData("run", "a/b")]
    [InlineData("run.", null)]
    [InlineData("run ", null)]
    [InlineData("CON", null)]
    [InlineData("nul.txt", null)]
    public void StartNewRun_ARunIdOrPhaseThatIsNotOneFolderName_ThrowsArgumentException(string runId, string? phase)
    {
        // "../logsX" used to pass: the escape check compared prefixes without a separator, and a sibling folder of the
        // logs folder starts with its path.
        var act = () => _provider.StartNewRun(runId, phase);

        act.Should().Throw<ArgumentException>().WithMessage("*not a single folder name*");
        Directory.GetFileSystemEntries(Path.GetDirectoryName(_tempDir)!, Path.GetFileName(_tempDir) + "X").Should().BeEmpty();
    }

    [Fact]
    public async Task StartNewRun_WhileTheSweepIsDeletingTheSameFolder_WaitsAndRecreatesIt()
    {
        // A run restarted under an id whose folder the sweep judged expired: without the shared lock the sweep would
        // delete the files the provider had just opened, and on Linux the run's logs would go nowhere, silently.
        var runRoot = Path.Combine(_tempDir, "restarted");
        Task? start = null;

        LogRunFolder.DeleteUnlessLive(runRoot, () =>
        {
            start = Task.Run(() => _provider.StartNewRun("restarted"));
            start.Wait(TimeSpan.FromMilliseconds(300)).Should().BeFalse("the run must wait for the sweep to finish");
            return true;
        }).Should().BeTrue();

        await start!.WaitAsync(TimeSpan.FromSeconds(10));
        File.Exists(Path.Combine(runRoot, LogRunFolder.LogFileName)).Should().BeTrue();
        LogRunFolder.IsLive(runRoot).Should().BeTrue();
    }

    [Fact]
    public void StartNewRun_WhenSomethingHoldsTheMarkersName_StillStarts()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "occupied", LogRunFolder.MarkerFileName));

        var act = () => _provider.StartNewRun("occupied");

        act.Should().NotThrow();
        _provider.IsRunActive.Should().BeTrue();
    }

    [Fact]
    public void StartNewRun_MarksTheRunAsTheProvidersOwn_AndReportsItAsCurrentUntilComplete()
    {
        _provider.StartNewRun("marked-run", "phase-1");

        LogRunFolder.IsMarkedRun(Path.Combine(_tempDir, "marked-run"), StringComparison.Ordinal).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "marked-run", "phase-1", LogRunFolder.MarkerFileName))
            .Should().BeFalse("the marker sits at the run root");
        LogRunFolder.IsLive(Path.Combine(_tempDir, "marked-run")).Should().BeTrue();

        _provider.CompleteRun();

        LogRunFolder.IsLive(Path.Combine(_tempDir, "marked-run")).Should().BeFalse();
    }

    [Fact]
    public void StartNewRun_EmptyBasePath_DoesNotActivateRun()
    {
        var config = Mock.Of<IOptionsMonitor<LoggingConfig>>(m =>
            m.CurrentValue == new LoggingConfig { LogsBasePath = "" });
        using var provider = new FileLoggerProvider(config, new PlainDirectoryCreator());

        provider.StartNewRun("run-1");

        provider.IsRunActive.Should().BeFalse();
    }

    [Fact]
    public void Log_WhenRunNotActive_DoesNotThrow()
    {
        var logger = _provider.CreateLogger("TestCategory");

        var act = () => logger.LogInformation("No run active");

        act.Should().NotThrow();
    }

    [Fact]
    public void Log_IsEnabledReturnsFalse_WhenRunNotActive()
    {
        var logger = _provider.CreateLogger("TestCategory");

        logger.IsEnabled(LogLevel.Information).Should().BeFalse();
    }

    [Fact]
    public void Log_WritesToBothFiles()
    {
        _provider.StartNewRun("run-files-test");
        var logger = _provider.CreateLogger("TestCategory");

        logger.LogInformation("Hello from test");

        // Give the background consumer thread time to process, then complete
        // the run which flushes any remaining queue items and disposes writers.
        Thread.Sleep(200);
        _provider.CompleteRun();

        var logPath = Path.Combine(_tempDir, "run-files-test", "log.txt");
        var consolePath = Path.Combine(_tempDir, "run-files-test", "console.txt");

        File.Exists(logPath).Should().BeTrue();
        File.Exists(consolePath).Should().BeTrue();

        var logContent = File.ReadAllText(logPath);
        var consoleContent = File.ReadAllText(consolePath);

        logContent.Should().Contain("Hello from test");
        consoleContent.Should().Contain("Hello from test");
    }

    [Fact]
    public void CompleteRun_WithManifest_WritesManifestJson()
    {
        _provider.StartNewRun("run-manifest-test");
        var logger = _provider.CreateLogger("TestCategory");
        logger.LogInformation("Entry 1");
        logger.LogInformation("Entry 2");

        Thread.Sleep(200);

        var manifest = new RunManifest
        {
            RunId = "run-manifest-test",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        _provider.CompleteRun(manifest);

        var manifestPath = Path.Combine(_tempDir, "run-manifest-test", "manifest.json");
        File.Exists(manifestPath).Should().BeTrue();

        var manifestContent = File.ReadAllText(manifestPath);
        manifestContent.Should().Contain("run-manifest-test");
        manifestContent.Should().Contain("logEntryCount");
    }

    [Fact]
    public void CompleteRun_SetsIsRunActiveToFalse()
    {
        _provider.StartNewRun("run-complete-test");
        _provider.IsRunActive.Should().BeTrue();

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
    public void StartNewRun_ClosesExistingRunFirst()
    {
        _provider.StartNewRun("run-1");
        var logger = _provider.CreateLogger("Test");
        logger.LogInformation("Run 1 message");

        Thread.Sleep(200);
        _provider.StartNewRun("run-2");

        // Run 1 files should have been flushed
        var logPath = Path.Combine(_tempDir, "run-1", "log.txt");
        File.Exists(logPath).Should().BeTrue();

        _provider.IsRunActive.Should().BeTrue();
    }

    [Fact]
    public void Log_WithException_IncludesExceptionInOutput()
    {
        _provider.StartNewRun("run-exception-test");
        var logger = _provider.CreateLogger("TestCategory");
        var ex = new InvalidOperationException("Test exception");

        logger.LogError(ex, "Error occurred");

        Thread.Sleep(200);
        _provider.CompleteRun();

        var logContent = File.ReadAllText(
            Path.Combine(_tempDir, "run-exception-test", "log.txt"));
        logContent.Should().Contain("Test exception");
        logContent.Should().Contain("Error occurred");
    }
}
