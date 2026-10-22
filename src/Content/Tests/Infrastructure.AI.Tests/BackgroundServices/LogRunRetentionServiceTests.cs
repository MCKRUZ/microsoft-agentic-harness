using Application.Common.Logging;
using Domain.Common.Config;
using Domain.Common.Models;
using FluentAssertions;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.BackgroundServices;

/// <summary>
/// Log runs age out (#901), driven through the real file providers so a file name they start writing tomorrow cannot be
/// left behind unnoticed: the run a provider is writing is kept, and a folder the providers did not mark is never reached.
/// </summary>
/// <remarks>
/// The clock starts well after the files are written, so a run written moments ago is already past the period; nothing has
/// to be backdated, which the providers' open files would not allow.
/// </remarks>
public sealed class LogRunRetentionServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly ObservableFakeClock _clock = new(DateTimeOffset.UtcNow.AddDays(60));
    private readonly AppConfig _config = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"log-retention-{Guid.NewGuid():N}");
    private readonly FileLoggerProvider _files;
    private readonly StructuredJsonLoggerProvider _json;

    public LogRunRetentionServiceTests()
    {
        Directory.CreateDirectory(_root);
        _config.Logging.LogsBasePath = _root;
        _config.Logging.Retention.SweepInterval = TimeSpan.FromHours(1);
        var logging = new Mock<IOptionsMonitor<LoggingConfig>>();
        logging.Setup(m => m.CurrentValue).Returns(() => _config.Logging);
        _files = new FileLoggerProvider(logging.Object, new PlainDirectoryCreator());
        _json = new StructuredJsonLoggerProvider(logging.Object, new PlainDirectoryCreator());
    }

    public void Dispose()
    {
        _files.Dispose();
        _json.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task ACompletedRun_IsDeletedWhole_EveryFileTheProvidersWrote()
    {
        WriteRun("finished", phase: "planning");

        await SweepAsync();

        Directory.Exists(Path.Combine(_root, "finished")).Should().BeFalse(
            "a file the providers write that the sweep does not know would keep the folder and its text forever");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRunAProviderIsWriting_IsKept(bool fileProvider)
    {
        // The live run has finished one phase (its files closed) and is writing the next. Windows refuses to delete the
        // open files, but not the closed ones, and Linux deletes open files too; only skipping the run keeps them.
        WriteRun("finished");
        WriteRun("live");
        if (fileProvider)
            _files.StartNewRun("live", "next-phase");
        else
            _json.StartNewRun("live", "next-phase");

        await SweepAsync();

        File.Exists(Path.Combine(_root, "live", LogRunFolder.LogFileName)).Should().BeTrue();
        File.Exists(Path.Combine(_root, "live", LogRunFolder.StructuredFileName)).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "finished")).Should().BeFalse();
    }

    [Fact]
    public async Task AFolderTheProvidersDidNotMark_IsNeverReached()
    {
        // The logs folder is also a root the file-system tool can write into.
        var foreign = Path.Combine(_root, "agent-notes");
        Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(foreign, LogRunFolder.LogFileName), "not a log run");

        await SweepAsync();

        File.Exists(Path.Combine(foreign, LogRunFolder.LogFileName)).Should().BeTrue();
    }

    [Fact]
    public async Task APeriodUnderADay_DeletesNothing()
    {
        _config.Logging.Retention.RetainFor = TimeSpan.FromHours(12);
        WriteRun("finished");

        await SweepAsync();

        Directory.Exists(Path.Combine(_root, "finished")).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, 30, 30)]
    [InlineData(false, 30, null)]
    [InlineData(true, 0.5, null)]
    public void TheReceiptPeriod_IsReportedOnlyWhileTheSweepRuns(bool enabled, double days, int? expectedDays)
    {
        _config.Logging.Retention.Enabled = enabled;
        _config.Logging.Retention.RetainFor = TimeSpan.FromDays(days);
        var expected = expectedDays is { } d ? TimeSpan.FromDays(d) : (TimeSpan?)null;

        _files.CurrentRetentionPeriod.Should().Be(expected);
        _json.CurrentRetentionPeriod.Should().Be(expected);
    }

    private void WriteRun(string runId, string? phase = null)
    {
        _files.StartNewRun(runId, phase);
        _json.StartNewRun(runId, phase);
        _files.CreateLogger("test").LogInformation("a line about someone");
        _json.CreateLogger("test").LogInformation("a line about someone");
        _files.CompleteRun(new RunManifest { RunId = runId, StartedAt = DateTimeOffset.UtcNow });
        _json.CompleteRun();
    }

    private async Task SweepAsync()
    {
        using var service = new LogRunRetentionService(Monitor(), _clock, NullLogger<LogRunRetentionService>.Instance);
        var armed = _clock.NextTimer;
        await service.StartAsync(CancellationToken.None);
        await armed.WaitAsync(Patience);
        var reArmed = _clock.NextTimer;
        _clock.Advance(TimeSpan.FromHours(1));
        await reArmed.WaitAsync(Patience);
        await service.StopAsync(CancellationToken.None);
    }

    private IOptionsMonitor<AppConfig> Monitor()
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        return monitor.Object;
    }
}
