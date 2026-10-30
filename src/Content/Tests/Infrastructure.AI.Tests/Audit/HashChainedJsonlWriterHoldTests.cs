using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.Core.Compliance;
using Application.Core.Validation;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Audit;
using Domain.Common.Models;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// Every audit trail's writer holds a record it could not append and replays it into the chain (#1001); an append made
/// while a person waits gives up on a busy chain quickly and lets the record be held instead (#999); and replay copes with
/// a held file that goes away between listing the folder and reading it (#1006).
/// </summary>
/// <remarks>
/// The day-file folder is made unwritable the way an unwritable folder would make it: a file stands where it belongs, so
/// every append fails at once rather than after the chain lock's ten-second wait.
/// </remarks>
public sealed class HashChainedJsonlWriterHoldTests : IDisposable
{
    /// <summary>Far below the writer's ten-second wait, far above the short bound plus a slow test machine.</summary>
    private static readonly TimeSpan Quickly = TimeSpan.FromSeconds(4);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"audit-hold-{Guid.NewGuid():N}");

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private AppConfig EveryTrail(string archivePath = "")
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                Governance = new GovernanceConfig { AuditStoragePath = Path.Combine(_root, "governance-root") },
                Audit = new AuditConfig
                {
                    PseudonymKeyPath = Path.Combine(_root, "keys", "audit-pseudonym.key"),
                    ArchivePath = archivePath,
                },
            },
        };
        config.AI.Governance.Escalation.AuditStoragePath = Path.Combine(_root, "escalation-root");
        config.AI.Changes.AuditStoragePath = Path.Combine(_root, "changes-root");
        config.AI.Egress.AuditStoragePath = Path.Combine(_root, "egress-root");
        config.AI.DriftDetection.AuditPath = Path.Combine(_root, "drift-root");
        config.AI.Erasure.AuditStoragePath = Path.Combine(_root, "erasure-root");
        return config;
    }

    private static readonly string[] Names = ["governance", "escalations", "changes", "egress", "drift", "compliance"];

    /// <summary>The six trails, by name: each is checked on its own so a missing one names itself.</summary>
    public static TheoryData<string> ChainNames() => new(Names);

    private AuditChainLocation Chain(string name) =>
        AuditChains.All(EveryTrail(), TimeProvider.System).Single(chain => chain.Name == name);

    private static void Break(AuditChainLocation chain)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(chain.Segments.Folder)!);
        File.WriteAllText(chain.Segments.Folder, "not a folder");
    }

    private static void Repair(AuditChainLocation chain) => File.Delete(chain.Segments.Folder);

    private static string[] Held(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*.pending").Order(StringComparer.Ordinal).ToArray() : [];

    // Carries the property each trail's records carry (action, timestamp, recorded_at), so every trail takes it as its own.
    private static string Record(string id) =>
        $"{{\"id\":\"{id}\",\"action\":\"probe\",\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"recorded_at\":\"{DateTimeOffset.UtcNow:O}\"}}";

    [Fact]
    public void AuditChains_EveryTrail_HasItsOwnPendingFolderBesideItsDayFolderInsideItsConfiguredFolder()
    {
        var config = EveryTrail();
        var chains = AuditChains.All(config, TimeProvider.System);

        chains.Select(c => c.Name).Should().BeEquivalentTo(Names);
        chains.Select(c => c.PendingPath).Should().OnlyHaveUniqueItems();
        foreach (var chain in chains)
        {
            chain.PendingPath.Should().Be(chain.Segments.Folder + "-pending",
                "a sibling of the day folder is never read as part of the chain or archived with it");
            Path.GetDirectoryName(chain.PendingPath).Should().StartWith(_root);
        }
    }

    [Fact]
    public void AuditChains_Compliance_KeepsThePendingFolderHeldRecordsAlreadyWaitIn()
    {
        var config = EveryTrail();

        AuditChains.Compliance(config, TimeProvider.System).PendingPath.Should().Be(
            Path.Combine(config.AI.Erasure.AuditStoragePath, ErasureConfig.PendingFolderName),
            "records held before #1001 must still be replayed after the upgrade");
    }

    [Theory]
    [MemberData(nameof(ChainNames))]
    public void AuditArchivePathValidator_AnArchiveInATrailsPendingFolder_IsRefused(string name)
    {
        var pending = Chain(name).PendingPath;

        var result = new AuditArchivePathValidator().Validate(EveryTrail(archivePath: Path.Combine(pending, "archive")).AI);

        result.IsValid.Should().BeFalse("held records read as archived ones would be a chain no witness covers");
    }

    [Theory]
    [MemberData(nameof(ChainNames))]
    public async Task AppendAsync_TheChainCannotBeWritten_HoldsTheRecordAndReplayWritesItIntoTheVerifiedChain(string name)
    {
        var chain = Chain(name);
        Break(chain);
        using var writer = new HashChainedJsonlWriter(chain, NullLogger.Instance);

        var appended = await writer.AppendAsync(Record("held-1"), CancellationToken.None);

        appended.IsSuccess.Should().BeFalse("the record is not in the chain yet");
        appended.Errors.Single().Should().Contain(chain.PendingPath);
        Held(chain.PendingPath).Should().ContainSingle();

        Repair(chain);
        var pass = await writer.ReplayPendingAsync(CancellationToken.None);

        pass.Written.Should().Be(1);
        Held(chain.PendingPath).Should().BeEmpty();
        var verified = await writer.VerifyChainAsync(CancellationToken.None);
        verified.IsValid.Should().BeTrue();
        verified.VerifiedCount.Should().Be(1, $"the {name} trail must not lose a record its chain refused once");
    }

    [Theory]
    [MemberData(nameof(ChainNames))]
    public async Task ReplayPendingAsync_AHeldFileThatIsNoRecordOfThisTrail_IsSetAsideNotWrittenIntoTheChain(string name)
    {
        var chain = Chain(name);
        Directory.CreateDirectory(chain.PendingPath);
        File.WriteAllText(Path.Combine(chain.PendingPath, "0000000000000000001-stray.pending"), "{\"foo\":1}");
        using var writer = new HashChainedJsonlWriter(chain, NullLogger.Instance);

        var pass = await writer.ReplayPendingAsync(CancellationToken.None);

        pass.SetAside.Should().Be(1, "a chain kept for good must not take in a file that is no record of it");
        (await writer.VerifyChainAsync(CancellationToken.None)).VerifiedCount.Should().Be(0);
    }

    [Fact]
    public async Task AppendWithoutHoldingAsync_TheChainCannotBeWritten_FailsAndHoldsNothing()
    {
        var chain = Chain("escalations");
        Break(chain);
        using var writer = new HashChainedJsonlWriter(chain, NullLogger.Instance);

        var appended = await writer.AppendWithoutHoldingAsync(Record("caller-retries"), CancellationToken.None);

        appended.IsSuccess.Should().BeFalse();
        Held(chain.PendingPath).Should().BeEmpty("a caller with its own durable retry would otherwise see the record twice");
    }

    [Fact]
    public async Task AppendAsync_AWriterBuiltFromBarePaths_HoldsNothing()
    {
        var chain = Chain("drift");
        Break(chain);
        using var writer = new HashChainedJsonlWriter(chain.Segments, chain.LockPath, NullLogger.Instance);

        var appended = await writer.AppendAsync(Record("not-held"), CancellationToken.None);

        appended.IsSuccess.Should().BeFalse();
        writer.PendingLocation.Should().BeNull();
        Held(chain.PendingPath).Should().BeEmpty("only a writer that knows its pending folder holds records");
    }

    [Fact]
    public async Task AppendAsync_ThePayloadBreaksTheFraming_IsRefusedAndNotHeld()
    {
        var chain = Chain("egress");
        using var writer = new HashChainedJsonlWriter(chain, NullLogger.Instance);

        var appended = await writer.AppendAsync("{\"a\":1}\n{\"b\":2}", CancellationToken.None);

        appended.IsSuccess.Should().BeFalse();
        Held(chain.PendingPath).Should().BeEmpty("replay could never append it either");
    }

    [Fact]
    public async Task RecordAsync_AnotherProcessHoldsTheChainLock_HoldsTheRecordWithoutTheTenSecondWait()
    {
        var config = EveryTrail();
        var chain = AuditChains.Compliance(config, TimeProvider.System);
        using var trail = NewComplianceTrail(config);
        Directory.CreateDirectory(chain.Segments.Folder);

        TimeSpan took;
        using (new FileStream(chain.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var clock = Stopwatch.StartNew();
            var result = await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
            took = clock.Elapsed;
            result.IsSuccess.Should().BeFalse();
        }

        took.Should().BeLessThan(Quickly, "a person's erasure must not wait out another writer's lock");
        Held(chain.PendingPath).Should().ContainSingle();
        (await trail.ReplayPendingAsync(CancellationToken.None)).Written.Should().Be(1);
        var verified = await trail.VerifyChainAsync(checkpoint: null, CancellationToken.None);
        verified.IsValid.Should().BeTrue();
        verified.VerifiedCount.Should().Be(1, "the record still lands in the verified chain");
    }

    [Fact]
    public async Task RecordAsync_ThisProcessIsStillAppendingToTheChain_HoldsTheRecordInsteadOfQueueingBehindIt()
    {
        var config = EveryTrail();
        var chain = AuditChains.Compliance(config, TimeProvider.System);
        using var trail = NewComplianceTrail(config);
        Directory.CreateDirectory(chain.Segments.Folder);

        var externalLock = new FileStream(chain.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task<PendingRecordsReplay> replay;
        TimeSpan took;
        try
        {
            await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
            // Replay appends with the full ten-second wait, so it takes this process's chain and waits on the other's lock.
            replay = trail.ReplayPendingAsync(CancellationToken.None);
            await ReplayIsAppendingAsync(chain);

            var clock = Stopwatch.StartNew();
            await trail.RecordAsync(Entry("outcome"), ErasureActor.DataSubject, CancellationToken.None);
            took = clock.Elapsed;
        }
        finally
        {
            await externalLock.DisposeAsync();
        }

        took.Should().BeLessThan(Quickly, "the bound covers this process's own appends and verifications, not only other processes");
        await replay;
        await trail.ReplayPendingAsync(CancellationToken.None);
        Held(chain.PendingPath).Should().BeEmpty();
        var verified = await trail.VerifyChainAsync(checkpoint: null, CancellationToken.None);
        verified.IsValid.Should().BeTrue();
        verified.VerifiedCount.Should().Be(2, "both records reach the verified chain once the chain is free");
    }

    [Fact]
    public async Task ReplayPendingAsync_AHeldFileGoneBetweenTheListingAndTheRead_IsNeitherStuckNorAnError()
    {
        var name = $"vanish-{Guid.NewGuid():N}";
        var shared = Chain("drift");
        var chain = shared with { Name = name };
        var logger = new LevelCapture();
        using var stuck = new StuckCapture(name);
        Break(chain);
        using var writer = new HashChainedJsonlWriter(chain, logger);
        await writer.AppendAsync(Record("goes-away"), CancellationToken.None);
        await writer.AppendAsync(Record("stays"), CancellationToken.None);
        Repair(chain);
        var gone = Held(chain.PendingPath)[0];
        var readHeld = writer.Pending!.ReadHeldFile;
        // As another host's pass would: the file is removed after this pass listed the folder, before it opens the file.
        writer.Pending.ReadHeldFile = path =>
        {
            if (path == gone)
                File.Delete(path);
            return readHeld(path);
        };
        logger.Clear();

        var pass = await writer.ReplayPendingAsync(CancellationToken.None);

        pass.Written.Should().Be(1, "the files after it still go ahead");
        pass.Remaining.Should().Be(0);
        stuck.Count.Should().Be(0, "a file that is gone is missing from nowhere; only one that cannot be opened is stuck");
        logger.Levels.Should().NotContain(level => level >= LogLevel.Error);
    }

    [Theory]
    [InlineData("0000000000000000001-held.pending")]
    [InlineData("0000000000000000001-held.unreadable")] // Set aside for an operator to repair and put back.
    public void AuditPseudonymKey_ANewKeyWhileOnlyAHeldRecordExists_SaysTheTrailsAlreadyHoldRecords(string heldFile)
    {
        var config = EveryTrail();
        var chain = AuditChains.Governance(config, TimeProvider.System);
        Directory.CreateDirectory(chain.PendingPath);
        File.WriteAllText(Path.Combine(chain.PendingPath, heldFile), Record("held"));
        var logger = new LevelCapture();

        AuditPseudonymKey.Resolve(config, new OwnerOnlyDirectoryCreator(), logger);

        logger.Levels.Should().Contain(LogLevel.Error,
            "a held record carries references made under the lost key and reaches the trail later");
    }

    private static async Task ReplayIsAppendingAsync(AuditChainLocation chain)
    {
        var replayLock = Path.Combine(chain.PendingPath, ".replay.lock");
        var deadline = DateTime.UtcNow + Quickly;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using (new FileStream(replayLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }
            }
            catch (FileNotFoundException)
            {
                // The pass has not made its lock yet.
            }
            catch (IOException)
            {
                // The pass holds its lock; give it a moment more to reach the append.
                await Task.Delay(300);
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The replay pass never started.");
    }

    private static JsonlComplianceAuditTrail NewComplianceTrail(AppConfig config) => new(
        new TestConfig.StaticOptionsMonitor<AppConfig>(config),
        TimeProvider.System,
        AuditPseudonyms.Create(),
        NullLogger<JsonlComplianceAuditTrail>.Instance);

    private static AuditEntry Entry(string phase) => new()
    {
        RequestType = "EraseMyDataCommand",
        Action = AuditedErasure.AuditAction,
        ExecutorId = "data-subject",
        CorrelationId = "corr-1",
        Timestamp = DateTimeOffset.UtcNow,
        Outcome = AuditOutcome.Attempted,
        Metadata = new Dictionary<string, string> { ["phase"] = phase },
    };

    /// <summary>The level of every entry logged while it is alive.</summary>
    private sealed class LevelCapture : ILogger
    {
        private readonly List<LogLevel> _levels = [];

        public IReadOnlyList<LogLevel> Levels
        {
            get
            {
                lock (_levels)
                    return [.. _levels];
            }
        }

        public void Clear()
        {
            lock (_levels)
                _levels.Clear();
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_levels)
                _levels.Add(logLevel);
        }
    }

    /// <summary>Counts <c>stuck</c> failures for one trail only, so tests running alongside cannot add to it.</summary>
    private sealed class StuckCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private int _count;

        public StuckCapture(string chainName)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == AuditConventions.WriteFailures)
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string? chain = null, outcome = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == AuditConventions.Chain)
                        chain = tag.Value as string;
                    else if (tag.Key == AuditConventions.Outcome)
                        outcome = tag.Value as string;
                }

                if (chain == chainName && outcome == AuditConventions.OutcomeValues.Stuck)
                    Interlocked.Increment(ref _count);
            });
            _listener.Start();
        }

        public int Count => Volatile.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }
}
