using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Domain.AI.Changes;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies that an approval-required tool call is actually put to a human, and that every way the
/// question can fail to produce a "yes" leaves the call blocked.
/// </summary>
/// <remarks>
/// The governor could always conclude "this needs approval" and never had anywhere to send it. These
/// tests pin the routing that closes that gap, and — more importantly — pin the fail-closed edges,
/// because a safety gate that quietly approves under load is worse than no gate at all.
/// </remarks>
public sealed class EscalationToolApprovalRouterTests
{
    private const string Agent = "test-agent";
    private const string Tool = "file_system";
    private const string Reason = "needs human sign-off";

    private readonly Mock<IEscalationService> _escalation = new();
    private readonly ICompositeResponseSanitizer _sanitizer =
        Mock.Of<ICompositeResponseSanitizer>(s =>
            s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()) == SanitizationResult.Clean("scrubbed"));

    private static GovernanceConfig Config(
        bool approvalEnabled = true,
        bool escalationEnabled = true,
        string[]? approvers = null,
        string timeoutAction = "DenyAndEscalate") =>
        new()
        {
            Escalation = new EscalationConfig
            {
                Enabled = escalationEnabled,
                DefaultTimeoutSeconds = 120,
                DefaultTimeoutAction = timeoutAction
            },
            ToolApproval = new ToolApprovalConfig
            {
                Enabled = approvalEnabled,
                Approvers = [.. approvers ?? ["alice"]]
            }
        };

    private EscalationToolApprovalRouter Build(GovernanceConfig config) => new(
        _escalation.Object,
        _sanitizer,
        Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == config),
        NullLogger<EscalationToolApprovalRouter>.Instance);

    private static EscalationOutcome Outcome(
        bool approved, EscalationResolutionType resolution, string approver = "alice") =>
        new()
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = approved,
            Decisions =
            [
                new ApproverDecision
                {
                    ApproverName = approver,
                    Approved = approved,
                    RespondedAt = DateTimeOffset.UtcNow
                }
            ],
            ResolutionType = resolution,
            ResolvedAt = DateTimeOffset.UtcNow
        };

    private void SetupEscalation(EscalationOutcome outcome) =>
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

    private ValueTask<ToolApprovalResult> Route(
        GovernanceConfig config,
        BlastRadius radius = BlastRadius.High,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default) =>
        Build(config).RequestApprovalAsync(Agent, Tool, Reason, radius, arguments, cancellationToken);

    [Fact]
    public async Task RequestApprovalAsync_RoutingDisabled_DoesNotAskAnyone()
    {
        var result = await Route(Config(approvalEnabled: false));

        Assert.Equal(ToolApprovalOutcome.NotRouted, result.Outcome);
        _escalation.Verify(x => x.RequestEscalationAsync(
            It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestApprovalAsync_EscalationSubsystemDisabled_DoesNotAskAnyone()
    {
        // Routing on, but the subsystem that delivers and resolves the request is off. Raising an
        // escalation nothing services would stall the turn to no purpose.
        var result = await Route(Config(escalationEnabled: false));

        Assert.Equal(ToolApprovalOutcome.NotRouted, result.Outcome);
        _escalation.Verify(x => x.RequestEscalationAsync(
            It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestApprovalAsync_NoApproversConfigured_DoesNotRaiseAnUnanswerableEscalation()
    {
        var result = await Route(Config(approvers: []));

        Assert.Equal(ToolApprovalOutcome.NotRouted, result.Outcome);
        Assert.Contains("no approvers", result.Reason, StringComparison.OrdinalIgnoreCase);
        _escalation.Verify(x => x.RequestEscalationAsync(
            It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestApprovalAsync_HumanApproves_AllowsTheCallAndNamesTheApprover()
    {
        SetupEscalation(Outcome(approved: true, EscalationResolutionType.Approved, approver: "alice"));

        var result = await Route(Config());

        Assert.Equal(ToolApprovalOutcome.Approved, result.Outcome);
        Assert.Contains("alice", result.Reason, StringComparison.Ordinal);
        Assert.NotNull(result.EscalationId);
    }

    [Theory]
    [InlineData(EscalationResolutionType.Denied)]
    [InlineData(EscalationResolutionType.TimedOut)]
    [InlineData(EscalationResolutionType.Escalated)]
    public async Task RequestApprovalAsync_AnythingOtherThanApproval_BlocksTheCall(
        EscalationResolutionType resolution)
    {
        SetupEscalation(Outcome(approved: false, resolution));

        var result = await Route(Config());

        Assert.Equal(ToolApprovalOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task RequestApprovalAsync_EscalationServiceThrows_BlocksTheCall()
    {
        // An unreachable approval subsystem must not become an open door. This is the fail-closed
        // edge the capability envelope and classification gate already hold.
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("escalation store unreachable"));

        var result = await Route(Config());

        Assert.Equal(ToolApprovalOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task RequestApprovalAsync_TurnCancelledWhileWaiting_BlocksRatherThanThrowing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await Route(Config(), cancellationToken: cts.Token);

        Assert.Equal(ToolApprovalOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task RequestApprovalAsync_HostConfiguredApproveOnTimeout_IsNotHonouredForToolCalls()
    {
        // The security-critical assertion in this file. EscalationTimeoutAction.Approve is a
        // legitimate global default for informational escalations, but inheriting it here would mean
        // a risky tool call proceeds *because* nobody was watching — a gate that fails open under
        // exactly the conditions it exists for. Tool approvals must always deny on timeout.
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: false, EscalationResolutionType.TimedOut));

        await Route(Config(timeoutAction: "Approve"));

        Assert.NotNull(captured);
        Assert.Equal(EscalationTimeoutAction.DenyAndEscalate, captured.TimeoutAction);
    }

    [Fact]
    public async Task RequestApprovalAsync_PutsTheActualArgumentsInFrontOfTheApprover()
    {
        // Approving the tool name "file_system" tells an approver nothing; approving a specific
        // path tells them everything. The arguments are the whole reason a human is worth asking.
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));

        await Route(Config(), arguments: new Dictionary<string, object?>
        {
            ["path"] = "/etc/passwd",
            ["mode"] = "delete"
        });

        Assert.NotNull(captured);
        Assert.Equal(Tool, captured.ToolName);
        Assert.Equal(Agent, captured.AgentId);
        Assert.Equal(2, captured.Arguments.Count);
        Assert.Contains("path", captured.Arguments.Keys);
        Assert.Contains("mode", captured.Arguments.Keys);
    }

    [Fact]
    public async Task RequestApprovalAsync_ArgumentsAreScrubbedBeforeAnyoneSeesThem()
    {
        // Arguments are model-influenced text that lands in a notification, a durable record, and an
        // audit line. They go through the same sanitizer chain that scrubs tool output.
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));

        await Route(Config(), arguments: new Dictionary<string, object?> { ["token"] = "sk-live-secret" });

        Assert.NotNull(captured);
        // The stub sanitizer rewrites everything to "scrubbed"; the raw value must not survive.
        Assert.Equal("scrubbed", captured.Arguments["token"]);
        Assert.DoesNotContain("sk-live-secret", string.Join("|", captured.Arguments.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestApprovalAsync_ManyArguments_AreCappedSoOneCallCannotFloodTheAuditTrail()
    {
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));

        var many = Enumerable.Range(0, 100)
            .ToDictionary(i => $"arg{i:D3}", i => (object?)i);

        await Route(Config(), arguments: many);

        Assert.NotNull(captured);
        // 32 capped arguments plus the one marker row explaining what was dropped.
        Assert.Equal(33, captured.Arguments.Count);
        Assert.Contains("(truncated)", captured.Arguments.Keys);
    }

    [Theory]
    [InlineData(BlastRadius.Low, EscalationPriority.Blocking)]
    [InlineData(BlastRadius.High, EscalationPriority.Blocking)]
    [InlineData(BlastRadius.Critical, EscalationPriority.Critical)]
    public async Task RequestApprovalAsync_BlastRadius_DrivesWhoGetsPaged(
        BlastRadius radius, EscalationPriority expected)
    {
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));

        await Route(Config(), radius);

        Assert.NotNull(captured);
        Assert.Equal(expected, captured.Priority);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData("-1")]
    public async Task RequestApprovalAsync_NumericCriticalThreshold_FallsBackToCriticalRatherThanBeingHonoured(
        string configured)
    {
        // #296. A bare Enum.TryParse accepts ANY integer string, including one outside the defined
        // range. "99" produced a BlastRadius of 99 — not a member — and the comparison is
        // `radius >= threshold`, so nothing could ever reach Critical priority again. The setting's
        // entire purpose, silently disabled by a typo, with no warning because parsing "succeeded".
        //
        // Critical is the safe fallback: it pages the widest audience, so a misconfiguration
        // over-notifies rather than under-notifies.
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));

        await Route(WithCriticalThreshold(configured), BlastRadius.Critical);

        Assert.NotNull(captured);
        Assert.Equal(EscalationPriority.Critical, captured.Priority);
    }

    [Fact]
    public async Task RequestApprovalAsync_NamedCriticalThreshold_IsStillHonoured()
    {
        // The control. Rejecting numeric forms must not have broken the values that always worked:
        // a threshold of High means a High-radius call pages at Critical priority.
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));

        await Route(WithCriticalThreshold("High"), BlastRadius.High);

        Assert.NotNull(captured);
        Assert.Equal(EscalationPriority.Critical, captured.Priority);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("99")]
    public async Task RequestApprovalAsync_NumericApprovalStrategy_FallsBackToADefinedStrategy(
        string configured)
    {
        // #296, the sharper half. DefaultEscalationService resolves the strategy from KEYED DI using
        // the enum value as the key, so an undefined value has no registered service and throws at
        // resolution — which this router's own fail-closed catch turns into a block. One mistyped
        // character would have refused every approval-required tool call for the life of the process.
        // ParseStrategy's config-string fallback is what keeps this router from ever constructing a
        // request with an undefined value in the first place; EscalationRequestInvariants rejects one
        // too, but only once a request already carries it (e.g. a hand-edited durable row).
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));

        await Route(WithApprovalStrategy(configured), BlastRadius.Low);

        Assert.NotNull(captured);
        Assert.True(Enum.IsDefined(captured.ApprovalStrategy),
            "an undefined strategy has no keyed service and throws when the escalation service resolves it");
        Assert.Equal(ApprovalStrategyType.AnyOf, captured.ApprovalStrategy);
    }

    [Fact]
    public async Task RequestApprovalAsync_NamedApprovalStrategy_IsStillHonoured()
    {
        // The control for the strategy half: a named strategy must survive unchanged, including the
        // quorum threshold it alone carries.
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));

        await Route(WithApprovalStrategy("Quorum"), BlastRadius.Low);

        Assert.NotNull(captured);
        Assert.Equal(ApprovalStrategyType.Quorum, captured.ApprovalStrategy);
        Assert.Equal(1, captured.QuorumThreshold);
    }

    private static GovernanceConfig WithCriticalThreshold(string criticalAtBlastRadius)
    {
        var config = Config();
        return new GovernanceConfig
        {
            Escalation = config.Escalation,
            ToolApproval = new ToolApprovalConfig
            {
                Enabled = true,
                Approvers = ["alice"],
                CriticalAtBlastRadius = criticalAtBlastRadius
            }
        };
    }

    private static GovernanceConfig WithApprovalStrategy(string strategy)
    {
        var config = Config();
        return new GovernanceConfig
        {
            Escalation = new EscalationConfig
            {
                Enabled = true,
                DefaultTimeoutSeconds = config.Escalation.DefaultTimeoutSeconds,
                DefaultTimeoutAction = config.Escalation.DefaultTimeoutAction,
                DefaultApprovalStrategy = strategy
            },
            ToolApproval = config.ToolApproval
        };
    }

    [Fact]
    public async Task RequestApprovalAsync_TimeoutOverride_BoundsHowLongATurnCanStall()
    {
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));

        var config = Config();
        var withOverride = new GovernanceConfig
        {
            Escalation = config.Escalation,
            ToolApproval = new ToolApprovalConfig
            {
                Enabled = true,
                Approvers = ["alice"],
                TimeoutSeconds = 15
            }
        };

        await Route(withOverride);

        Assert.NotNull(captured);
        Assert.Equal(15, captured.TimeoutSeconds);
    }

    [Fact]
    public async Task RequestApprovalAsync_NoTimeoutOverride_InheritsTheEscalationDefault()
    {
        EscalationRequest? captured = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));

        await Route(Config());

        Assert.NotNull(captured);
        Assert.Equal(120, captured.TimeoutSeconds);
    }
}
