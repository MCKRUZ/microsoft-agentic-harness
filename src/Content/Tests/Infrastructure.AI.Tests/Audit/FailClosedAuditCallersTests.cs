using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Core.Compliance;
using Domain.AI.Changes;
using Domain.AI.DriftDetection;
using Domain.AI.Egress;
using Domain.AI.Escalation;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Models;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Changes;
using Infrastructure.AI.DriftDetection;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Telemetry.Redaction;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Common;
using Xunit;
using GateAction = Domain.AI.Changes.GateAction;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// One guard over the whole class (#1001): a caller that refuses its action when the record cannot be written, or that
/// writes the record again itself, must never find that record replayed as if the action went ahead. Every audit write
/// entry point is classified by what its callers do on failure, the classification is pinned against every call site in
/// the production code, and each entry point is driven against an unwritable trail.
/// </summary>
/// <remarks>
/// <para>
/// <strong>How the callers are found.</strong> For each trail's interface, every production file that names it, and every
/// partial of the same class beside it (a partial that only uses an inherited field names no type), is searched for that
/// trail's write calls. A call site that is not in <see cref="CallSites"/> fails <see cref="EveryCallSite_IsClassified"/>, so
/// a new caller is classified before it ships, and one that went away has to leave the list.
/// </para>
/// <para>
/// <strong>Not covered.</strong> The governance trail's <c>Log</c> returns nothing and never throws, so no caller can refuse on
/// it; it is not listed. A caller reaching a store through a variable whose file names no audit interface is not found.
/// </para>
/// </remarks>
public sealed class FailClosedAuditCallersTests : IDisposable
{
    /// <summary>What a caller does when its record cannot be written.</summary>
    public enum OnFailure
    {
        /// <summary>Refuses the action: the record must not be replayed as one that took effect.</summary>
        Refuses,

        /// <summary>Re-drives the same record itself until it is written: holding it too would write it twice.</summary>
        ReDrives,

        /// <summary>Carries on, the action having happened: the record must be held, or it is lost.</summary>
        Proceeds,
    }

    /// <summary>Every production call of a trail's write entry point, as (trail, file under src/Content, call, policy).</summary>
    private static readonly (string Trail, string File, string Call, OnFailure Policy)[] CallSites =
    [
        ("egress", "Infrastructure/Infrastructure.AI/Egress/EgressPolicyDelegatingHandler.cs", "AppendAsync", OnFailure.Refuses),
        ("egress", "Infrastructure/Infrastructure.AI/Egress/EgressPolicyDelegatingHandler.cs", "AppendAsync", OnFailure.Refuses),
        ("egress", "Infrastructure/Infrastructure.AI/Sandbox/SandboxEgressPreflight.cs", "AppendAsync", OnFailure.Refuses),
        ("changes", "Application/Application.AI.Common/CQRS/Changes/ChangeProposalCommandHelper.cs", "AppendAsync", OnFailure.Refuses),
        ("changes", "Infrastructure/Infrastructure.AI/Changes/ChangeProposalOrchestrator.cs", "AppendAsync", OnFailure.Refuses),
        ("changes", "Infrastructure/Infrastructure.AI/Changes/ChangeProposalOrchestrator.cs", "AppendAsync", OnFailure.Refuses),
        ("escalations", "Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.cs", "RecordRequestAsync", OnFailure.Refuses),
        ("escalations", "Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.cs", "RecordDecisionAsync", OnFailure.Refuses),
        ("escalations", "Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.Resolution.cs", "RecordOutcomeAsync", OnFailure.ReDrives),
        ("escalations", "Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.Durability.cs", "RecordOutcomeAsync", OnFailure.ReDrives),
        ("escalations", "Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.Durability.cs", "RecordOutcomeAsync", OnFailure.ReDrives),
        ("escalations", "Application/Application.AI.Common/Services/Escalation/DefaultApprovalExecutionReporter.cs", "RecordExecutionAsync", OnFailure.Proceeds),
        ("drift", "Application/Application.Core/CQRS/DriftDetection/DriftOperatorAuditRecorder.cs", "RecordBeforeActingAsync", OnFailure.Refuses),
        ("drift", "Application/Application.Core/CQRS/DriftDetection/DriftOperatorAuditRecorder.cs", "RecordAsync", OnFailure.Proceeds),
        ("drift", "Infrastructure/Infrastructure.AI/DriftDetection/DefaultDriftDetectionService.cs", "RecordAsync", OnFailure.Proceeds),
        ("drift", "Infrastructure/Infrastructure.AI/Learnings/LearningsDriftBridge.cs", "RecordAsync", OnFailure.Proceeds),
        ("compliance", "Application/Application.Core/Compliance/ComplianceRecords.cs", "RecordAsync", OnFailure.Proceeds),
    ];

    /// <summary>Each trail's interface, the store that implements it, and the calls that write to it.</summary>
    private static readonly (string Trail, string Interface, string Implementation, Regex Calls)[] Trails =
    [
        ("egress", "IEgressAuditWriter", "JsonlEgressAuditWriter.cs", new(@"\.(AppendAsync)\(")),
        ("changes", "IChangeAuditWriter", "JsonlChangeAuditWriter.cs", new(@"\.(AppendAsync)\(")),
        ("escalations", "IEscalationAuditStore", "JsonlEscalationAuditStore.cs",
            new(@"\.(RecordRequestAsync|RecordDecisionAsync|RecordOutcomeAsync|RecordExecutionAsync)\(")),
        ("drift", "IDriftAuditStore", "JsonlDriftAuditStore.cs", new(@"\.(RecordAsync|RecordBeforeActingAsync)\(")),
        // Only the trail's own receiver: the same files write the general audit sink with a RecordAsync of its own.
        ("compliance", "IComplianceAuditTrail", "JsonlComplianceAuditTrail.cs", new(@"\btrail\.(RecordAsync)\(")),
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fail-closed-{Guid.NewGuid():N}");

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void EveryCallSite_IsClassified()
    {
        var found = Trails.SelectMany(trail => CallsOf(trail.Trail, trail.Interface, trail.Implementation, trail.Calls))
            .Order(StringComparer.Ordinal).ToList();
        var listed = CallSites.Select(c => $"{c.Trail} {c.File} {c.Call}").Order(StringComparer.Ordinal).ToList();

        found.Should().Equal(listed,
            "a caller of an audit write must be classified by what it does when the record cannot be written");
    }

    [Fact]
    public void EveryEntryPoint_HasCallersThatAgree()
    {
        // The store cannot tell one caller from another, so all callers of an entry point must want the same thing.
        CallSites.GroupBy(c => (c.Trail, c.Call))
            .Where(g => g.Select(c => c.Policy == OnFailure.Proceeds).Distinct().Count() > 1)
            .Select(g => $"{g.Key.Trail} {g.Key.Call}")
            .Should().BeEmpty();
    }

    public static TheoryData<string, string> EntryPoints()
    {
        var data = new TheoryData<string, string>();
        foreach (var (trail, call) in CallSites.Select(c => (c.Trail, c.Call)).Distinct())
            data.Add(trail, call);
        return data;
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task EachEntryPoint_TheTrailCannotBeWritten_HoldsOnlyWhatItsCallersLeaveUnwritten(string trail, string call)
    {
        var policy = CallSites.First(c => c.Trail == trail && c.Call == call).Policy;
        var config = Config();
        var chain = AuditChains.All(config, TimeProvider.System).Single(c => c.Name == trail);
        Directory.CreateDirectory(Path.GetDirectoryName(chain.Segments.Folder)!);
        File.WriteAllText(chain.Segments.Folder, "not a folder");

        try
        {
            await WriteAsync(config, trail, call);
        }
        catch (IOException)
        {
            // A refusing trail throws; whether it held anything is what is checked below.
        }

        var held = Directory.Exists(chain.PendingPath)
            ? Directory.GetFiles(chain.PendingPath, "*.pending").Select(File.ReadAllText).ToList()
            : [];
        if (policy == OnFailure.Proceeds)
            held.Should().ContainSingle($"{trail} {call}: nothing else would ever write this record");
        else
            held.Should().OnlyContain(line => line.Contains("\"not_carried_out\":true"),
                $"{trail} {call}: a record whose caller refuses or re-drives it must never be replayed as one that took effect");
    }

    private static IEnumerable<string> CallsOf(string trail, string interfaceName, string implementation, Regex calls)
    {
        var content = RepoRoot.Combine("src", "Content");
        var production = Directory.EnumerateFiles(content, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        var naming = production.Where(path => !path.EndsWith(implementation, StringComparison.Ordinal)
            && File.ReadAllText(path).Contains(interfaceName, StringComparison.Ordinal)).ToList();

        // A partial that only uses the field another partial declares names no interface; take the whole class.
        var files = naming
            .SelectMany(path => production.Where(other =>
                string.Equals(Path.GetDirectoryName(other), Path.GetDirectoryName(path), StringComparison.Ordinal)
                && (other == path || Path.GetFileName(other).StartsWith(Path.GetFileNameWithoutExtension(path) + ".", StringComparison.Ordinal))))
            .Distinct()
            .Where(path => !path.EndsWith(implementation, StringComparison.Ordinal));

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(content, file).Replace(Path.DirectorySeparatorChar, '/');
            foreach (Match match in calls.Matches(StripComments(File.ReadAllText(file))))
                yield return $"{trail} {relative} {match.Groups[1].Value}";
        }
    }

    // Doc comments and line comments name calls too; only code counts.
    private static string StripComments(string source) => Regex.Replace(source, @"//.*$", string.Empty, RegexOptions.Multiline);

    private AppConfig Config()
    {
        var config = new AppConfig
        {
            AI = new AIConfig { Governance = new GovernanceConfig { AuditStoragePath = Path.Combine(_root, "governance") } },
        };
        config.AI.Governance.Escalation.AuditStoragePath = Path.Combine(_root, "escalations");
        config.AI.Changes.AuditStoragePath = Path.Combine(_root, "changes");
        config.AI.Egress.AuditStoragePath = Path.Combine(_root, "egress");
        config.AI.DriftDetection.AuditPath = Path.Combine(_root, "drift");
        config.AI.Erasure.AuditStoragePath = Path.Combine(_root, "erasure");
        return config;
    }

    private static async Task WriteAsync(AppConfig config, string trail, string call)
    {
        var monitor = new TestConfig.StaticOptionsMonitor<AppConfig>(config);
        var pseudonyms = AuditPseudonyms.Create();
        switch (trail, call)
        {
            case ("egress", _):
                using (var egress = new JsonlEgressAuditWriter(monitor, TimeProvider.System, NullLogger<JsonlEgressAuditWriter>.Instance, pseudonyms))
                    await egress.AppendAsync(EgressDecision(), Egress.Support.TestIdentity.Default, CancellationToken.None);
                break;
            case ("changes", _):
                using (var changes = new JsonlChangeAuditWriter(monitor, TimeProvider.System,
                           NullLogger<JsonlChangeAuditWriter>.Instance, pseudonyms, new DefaultContentRedactionFilter()))
                {
                    var proposal = TestProposals.NewProposal();
                    await changes.AppendAsync(proposal, GateDecision(), proposal.SubmittedBy,
                        Application.AI.Common.Interfaces.Changes.OrchestratorMode.Live, "corr-1", CancellationToken.None);
                }

                break;
            case ("escalations", _):
                using (var escalations = new JsonlEscalationAuditStore(monitor, TimeProvider.System,
                           NullLogger<JsonlEscalationAuditStore>.Instance, pseudonyms, new DefaultContentRedactionFilter()))
                    await WriteEscalationAsync(escalations, call);
                break;
            case ("drift", _):
                using (var drift = new JsonlDriftAuditStore(monitor, TimeProvider.System, NullLogger<JsonlDriftAuditStore>.Instance))
                {
                    var record = DriftRecord();
                    _ = call == "RecordBeforeActingAsync"
                        ? await drift.RecordBeforeActingAsync(record, CancellationToken.None)
                        : await drift.RecordAsync(record, CancellationToken.None);
                }

                break;
            case ("compliance", _):
                using (var compliance = new JsonlComplianceAuditTrail(monitor, TimeProvider.System, pseudonyms,
                           NullLogger<JsonlComplianceAuditTrail>.Instance))
                    await compliance.RecordAsync(ComplianceEntry(), ErasureActor.DataSubject, CancellationToken.None);
                break;
            default:
                throw new InvalidOperationException($"No way to drive {trail} {call}; add one.");
        }
    }

    private static Task WriteEscalationAsync(JsonlEscalationAuditStore store, string call)
    {
        var id = Guid.NewGuid();
        var decision = new ApproverDecision
        {
            ApproverName = "admin", Verdict = ApproverVerdict.Approve, Reason = "ok", RespondedAt = DateTimeOffset.UtcNow,
        };
        return call switch
        {
            "RecordRequestAsync" => store.RecordRequestAsync(new EscalationRequest
            {
                EscalationId = id, AgentId = "agent-1", ToolName = "delete_file", Arguments = new Dictionary<string, string>(),
                Description = "d", RiskLevel = RiskLevel.Medium, Priority = EscalationPriority.Blocking, Approvers = ["admin"],
                RequestedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None),
            "RecordDecisionAsync" => store.RecordDecisionAsync(id, decision, CancellationToken.None),
            "RecordOutcomeAsync" => store.RecordOutcomeAsync(new EscalationOutcome
            {
                EscalationId = id, IsApproved = true, Decisions = [decision], ResolutionType = EscalationResolutionType.Approved,
                ResolvedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None),
            "RecordExecutionAsync" => store.RecordExecutionAsync(
                EscalationExecutionRecord.Succeeded(id, DateTimeOffset.UtcNow, "agent-turn"), CancellationToken.None),
            _ => throw new InvalidOperationException($"No way to drive escalations {call}; add one."),
        };
    }

    private static EgressDecision EgressDecision() => new()
    {
        Allowed = true,
        Reason = "Matched allowlist entry.",
        MatchedAllowlistEntry = "api.github.com",
        Target = new Uri("https://api.github.com/users/octocat"),
        DecidedAt = DateTimeOffset.UtcNow,
    };

    private static GateDecision GateDecision() => new()
    {
        Timestamp = TestProposals.DefaultTime, GateKey = "approval", Action = GateAction.Pass, Reason = string.Empty, DurationMs = 1,
    };

    private static DriftAuditRecord DriftRecord() => new()
    {
        RecordId = Guid.NewGuid(),
        EventId = Guid.NewGuid(),
        RecordType = DriftAuditRecordType.Detected,
        Payload = JsonSerializer.Serialize(new { detail = "test" }),
        RecordedAt = DateTimeOffset.UtcNow,
    };

    private static AuditEntry ComplianceEntry() => new()
    {
        RequestType = "EraseMyDataCommand",
        Action = AuditedErasure.AuditAction,
        ExecutorId = "data-subject",
        CorrelationId = Guid.NewGuid().ToString("N"),
        Timestamp = DateTimeOffset.UtcNow,
        Outcome = AuditOutcome.Attempted,
        Metadata = new Dictionary<string, string> { ["phase"] = "attempt" },
    };
}
