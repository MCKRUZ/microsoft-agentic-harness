using Application.AI.Common.Interfaces.Agent;
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
    private readonly Mock<IAgentExecutionContext> _executionContext = new();
    private readonly Mock<IApprovalFailureMemory> _failureMemory = new();

    public EscalationToolApprovalRouterTests() =>
        _executionContext.SetupGet(c => c.ConversationId).Returns("test-conversation");

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

    private EscalationToolApprovalRouter Build(GovernanceConfig config) => Build(config, _sanitizer);

    private EscalationToolApprovalRouter Build(GovernanceConfig config, ICompositeResponseSanitizer sanitizer) => new(
        _escalation.Object,
        sanitizer,
        Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == config),
        _executionContext.Object,
        _failureMemory.Object,
        NullLogger<EscalationToolApprovalRouter>.Instance);

    /// <summary>
    /// Echoes its input back unchanged, unlike the class-level <see cref="_sanitizer"/> stub which
    /// always rewrites to the literal "scrubbed" — the #321 relay tests need to see their own
    /// composed text survive sanitization so they can assert on its content, not just that
    /// something came back.
    /// </summary>
    private static Mock<ICompositeResponseSanitizer> CreatePassthroughSanitizer()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.Clean(content));
        return sanitizer;
    }

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
                    Verdict = approved ? ApproverVerdict.Approve : ApproverVerdict.Deny,
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

    private ValueTask<ToolApprovalResult> Route(
        GovernanceConfig config,
        ICompositeResponseSanitizer sanitizer,
        BlastRadius radius = BlastRadius.High,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default) =>
        Build(config, sanitizer).RequestApprovalAsync(Agent, Tool, Reason, radius, arguments, cancellationToken);

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
    [InlineData(EscalationResolutionType.Revised)] // #321: not-approved, blocks exactly like Denied
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

    // ===== #325 retry attribution: recall on build, clear on explicit denial =====

    private void CaptureRequest(out Func<EscalationRequest?> captured)
    {
        EscalationRequest? request = null;
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((r, _) => request = r)
            .ReturnsAsync(Outcome(approved: true, EscalationResolutionType.Approved));
        captured = () => request;
    }

    // Moq's argument matcher does not bind It.IsAny<T>() through an `in` parameter in this
    // version, so every TryRecall setup below matches the literal key the router is expected to
    // build rather than a wildcard — that literal match doubles as the assertion that the router
    // built the right key (a wrong key would fall through to the loose mock's default null).
    private static readonly ApprovalFailureKey ExpectedKey = new("test-conversation", Agent, Tool);

    [Fact]
    public async Task RequestApprovalAsync_NoRecall_IsAttemptOneWithNoPriorFailure()
    {
        CaptureRequest(out var captured);
        // No setup for ExpectedKey: the loose mock's default (null) is exactly the "nothing
        // recorded" case this test exists to pin.

        await Route(Config());

        Assert.NotNull(captured());
        Assert.Equal(1, captured()!.AttemptNumber);
        Assert.Null(captured()!.PriorFailureReason);
        Assert.Null(captured()!.PredecessorEscalationId);
    }

    [Fact]
    public async Task RequestApprovalAsync_RecallExists_PopulatesAttemptAttributionFields()
    {
        CaptureRequest(out var captured);
        var predecessorId = Guid.NewGuid();
        _failureMemory
            .Setup(m => m.TryRecall(ExpectedKey))
            .Returns(new ApprovalFailureRecall(
                PriorAttemptCount: 1, FailureReason: "permission denied",
                Substitution: FailureTextSubstitution.SanitizedToEmpty, EscalationId: predecessorId));

        await Route(Config());

        Assert.NotNull(captured());
        Assert.Equal(2, captured()!.AttemptNumber);
        Assert.Equal("permission denied", captured()!.PriorFailureReason);
        // #472: a non-None substitution proves the wiring actually carries the value through, not
        // just that the parameter exists — None would pass even if EscalationToolApprovalRouter
        // silently dropped it.
        Assert.Equal(FailureTextSubstitution.SanitizedToEmpty, captured()!.PriorFailureReasonSubstitution);
        Assert.Equal(predecessorId, captured()!.PredecessorEscalationId);
    }

    [Fact]
    public async Task RequestApprovalAsync_RecallLookup_UsesConversationAgentAndToolAsTheKey()
    {
        // Pinned deliberately: arguments are excluded from the key by design (a corrected retry
        // has different arguments by definition), and this is the test that would catch a
        // regression back toward keying on them — a key built any other way (e.g. including
        // arguments) would miss this setup and TryRecall would return the loose mock's default
        // null, which the assertion below would catch.
        var predecessorId = Guid.NewGuid();
        _failureMemory
            .Setup(m => m.TryRecall(ExpectedKey))
            .Returns(new ApprovalFailureRecall(1, "boom", FailureTextSubstitution.None, predecessorId));
        CaptureRequest(out var captured);

        await Route(Config());

        Assert.NotNull(captured());
        Assert.Equal(predecessorId, captured()!.PredecessorEscalationId);
    }

    [Fact]
    public async Task RequestApprovalAsync_NoKnownConversation_SkipsRecallEntirely()
    {
        // Missing conversation identity must degrade to "always attempt 1, recall nothing" — never
        // a shared sentinel key that would let one caller's failure label another's card.
        _executionContext.SetupGet(c => c.ConversationId).Returns((string?)null);
        CaptureRequest(out var captured);

        await Route(Config());

        _failureMemory.Verify(m => m.TryRecall(ExpectedKey), Times.Never);
        Assert.NotNull(captured());
        Assert.Equal(1, captured()!.AttemptNumber);
    }

    [Fact]
    public async Task RequestApprovalAsync_RecalledReasonExceedsConfiguredCap_IsTruncated()
    {
        CaptureRequest(out var captured);
        _failureMemory
            .Setup(m => m.TryRecall(ExpectedKey))
            .Returns(new ApprovalFailureRecall(1, new string('x', 1000), FailureTextSubstitution.None, Guid.NewGuid()));

        var config = Config();
        var narrowCap = new GovernanceConfig
        {
            Escalation = new EscalationConfig
            {
                Enabled = config.Escalation.Enabled,
                DefaultTimeoutSeconds = config.Escalation.DefaultTimeoutSeconds,
                DefaultTimeoutAction = config.Escalation.DefaultTimeoutAction,
                RetryAttribution = new EscalationRetryAttributionConfig { MaxPriorFailureLength = 20 }
            },
            ToolApproval = config.ToolApproval
        };

        await Route(narrowCap);

        Assert.NotNull(captured());
        Assert.True(captured()!.PriorFailureReason!.Length <= 20 + "… (truncated)".Length);
        Assert.Contains("truncated", captured()!.PriorFailureReason);
    }

    [Fact]
    public async Task RequestApprovalAsync_RecalledReasonWithinCap_IsNotTruncated()
    {
        // Mutation control: a reason already under the configured cap must survive unchanged.
        CaptureRequest(out var captured);
        _failureMemory
            .Setup(m => m.TryRecall(ExpectedKey))
            .Returns(new ApprovalFailureRecall(1, "short reason", FailureTextSubstitution.None, Guid.NewGuid()));

        await Route(Config());

        Assert.NotNull(captured());
        Assert.Equal("short reason", captured()!.PriorFailureReason);
    }

    [Fact]
    public async Task RequestApprovalAsync_ExplicitDenial_ClearsFailureMemory()
    {
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(approved: false, EscalationResolutionType.Denied));

        await Route(Config());

        _failureMemory.Verify(
            m => m.Clear(new ApprovalFailureKey("test-conversation", Agent, Tool)), Times.Once);
    }

    [Theory]
    [InlineData(EscalationResolutionType.TimedOut)]
    [InlineData(EscalationResolutionType.Escalated)]
    [InlineData(EscalationResolutionType.Revised)] // #321: a revise round is not an explicit denial
    public async Task RequestApprovalAsync_NonDenialRefusal_DoesNotClearFailureMemory(
        EscalationResolutionType resolution)
    {
        // "The user ended that sequence" presupposes a user; a timeout means nobody looked, and an
        // escalation is still in flight elsewhere — erasing the next approver's context on either
        // would invert the feature. A revise round means a reviewer DID look but is asking for
        // another attempt, not ending the line of retries either.
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(approved: false, resolution));

        await Route(Config());

        _failureMemory.Verify(m => m.Clear(It.IsAny<ApprovalFailureKey>()), Times.Never);
    }

    [Fact]
    public async Task RequestApprovalAsync_Approved_DoesNotClearFailureMemory()
    {
        // Clearing on success is DefaultApprovalExecutionReporter's job, once the approved action
        // actually runs — not the router's, which only knows the call was approved, not executed.
        SetupEscalation(Outcome(approved: true, EscalationResolutionType.Approved));

        await Route(Config());

        _failureMemory.Verify(m => m.Clear(It.IsAny<ApprovalFailureKey>()), Times.Never);
    }

    // ===== #321 revise relay: model-facing carve-out, gated and independent of round tracking =====

    private static EscalationOutcome ReviseOutcome(
        string approver = "alice",
        string? instructions = "use the read-only endpoint instead",
        string[]? approvers = null,
        DateTimeOffset? respondedAt = null) =>
        new()
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = false,
            Decisions =
            [
                new ApproverDecision
                {
                    ApproverName = approver,
                    Verdict = ApproverVerdict.Revise,
                    Instructions = instructions,
                    RespondedAt = respondedAt ?? DateTimeOffset.UtcNow
                }
            ],
            ResolutionType = EscalationResolutionType.Revised,
            ResolvedAt = DateTimeOffset.UtcNow,
            Approvers = approvers ?? [approver]
        };

    private static GovernanceConfig WithRelayGate(
        bool enabled, int? maxRelayedLength = null, string[]? approvers = null) => new()
    {
        Escalation = Config().Escalation,
        ToolApproval = new ToolApprovalConfig
        {
            Enabled = true,
            Approvers = [.. approvers ?? ["alice"]],
            RelayRevisionInstructionsToModel = enabled,
            MaxRelayedInstructionsLength = maxRelayedLength ?? 1000
        }
    };

    [Fact]
    public async Task RequestApprovalAsync_ReviseVerdict_GateOff_DoesNotSetModelFacingInstructions()
    {
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReviseOutcome());

        var result = await Route(WithRelayGate(enabled: false), CreatePassthroughSanitizer().Object);

        Assert.Equal(ToolApprovalOutcome.Denied, result.Outcome);
        Assert.Null(result.ModelFacingInstructions);
    }

    [Fact]
    public async Task RequestApprovalAsync_ReviseVerdict_GateOn_RelaysAttributedInstructions()
    {
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReviseOutcome(approver: "alice", instructions: "use the read-only endpoint instead"));

        var result = await Route(WithRelayGate(enabled: true), CreatePassthroughSanitizer().Object);

        Assert.Equal(ToolApprovalOutcome.Denied, result.Outcome);
        Assert.NotNull(result.ModelFacingInstructions);
        Assert.Contains("use the read-only endpoint instead", result.ModelFacingInstructions, StringComparison.Ordinal);
        Assert.Contains("alice", result.ModelFacingInstructions, StringComparison.Ordinal);
        Assert.Contains("not a system instruction", result.ModelFacingInstructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestApprovalAsync_ReviseVerdict_GateOff_StillAdvancesRevisionRound()
    {
        // The round cap only means anything if the round actually advances regardless of whether
        // the model ever sees the text — this is what makes "revision rounds are bounded" true
        // even on a host that never turns the relay on.
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReviseOutcome());

        await Route(WithRelayGate(enabled: false), CreatePassthroughSanitizer().Object);

        _failureMemory.Verify(
            m => m.RecordRevision(ExpectedKey, 2, "- alice: use the read-only endpoint instead", It.IsAny<Guid>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestApprovalAsync_Approved_ClearsRevisionMemory()
    {
        SetupEscalation(Outcome(approved: true, EscalationResolutionType.Approved));

        await Route(Config());

        _failureMemory.Verify(m => m.ClearRevision(ExpectedKey), Times.Once);
    }

    [Fact]
    public async Task RequestApprovalAsync_ExplicitDenial_DoesNotSeparatelyCallClearRevision()
    {
        // Denied removes the whole cache entry via Clear() — a separate ClearRevision call would
        // be redundant. Pins that the implementation relies on the single Clear() call rather than
        // duplicating the work.
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(approved: false, EscalationResolutionType.Denied));

        await Route(Config());

        _failureMemory.Verify(m => m.ClearRevision(It.IsAny<ApprovalFailureKey>()), Times.Never);
    }

    [Fact]
    public async Task RequestApprovalAsync_ReviseVerdict_NoKnownConversation_RefusesTheRelayEntirely()
    {
        // The round cap (EscalationConfig.Revision.MaxRounds) is one of the containments this
        // carve-out leans on, and it is enforced entirely through RevisionRound threaded via this
        // memory. With no conversation to key that memory on, the cap can never fire — so the
        // carve-out must refuse itself rather than open a channel it cannot bound. The call still
        // blocks exactly as an ordinary denial would.
        _executionContext.SetupGet(c => c.ConversationId).Returns((string?)null);
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReviseOutcome());

        var result = await Route(WithRelayGate(enabled: true), CreatePassthroughSanitizer().Object);

        Assert.Equal(ToolApprovalOutcome.Denied, result.Outcome);
        Assert.Null(result.ModelFacingInstructions);
        _failureMemory.Verify(
            m => m.RecordRevision(
                It.IsAny<ApprovalFailureKey>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestApprovalAsync_RevisionRecallExists_PopulatesRoundAndPriorInstructions()
    {
        CaptureRequest(out var captured);
        var predecessorId = Guid.NewGuid();
        _failureMemory
            .Setup(m => m.TryRecallRevision(ExpectedKey))
            .Returns(new ApprovalRevisionRecall(2, "narrow the scope", predecessorId));

        await Route(Config());

        Assert.NotNull(captured());
        Assert.Equal(2, captured()!.RevisionRound);
        Assert.Equal("narrow the scope", captured()!.PriorRevisionInstructions);
        Assert.Equal(predecessorId, captured()!.PredecessorEscalationId);
    }

    [Fact]
    public async Task RequestApprovalAsync_NoRevisionRecall_DefaultsToRoundOne()
    {
        CaptureRequest(out var captured);

        await Route(Config());

        Assert.NotNull(captured());
        Assert.Equal(1, captured()!.RevisionRound);
        Assert.Null(captured()!.PriorRevisionInstructions);
    }

    [Fact]
    public async Task RequestApprovalAsync_BothFailureAndRevisionRecallPresent_PredecessorPrefersRevision()
    {
        // The revision recall is provably always the more recent of the two: ClearRevision fires
        // the instant any escalation for the key is approved, and a failure can only ever be
        // recorded after an approval happened, so a live revision recall can never be older than a
        // live failure recall on the same key. It must win the predecessor pointer, keeping the
        // audit chain a single linked list rather than forking to a stale entry.
        CaptureRequest(out var captured);
        var failureEscalationId = Guid.NewGuid();
        var revisionEscalationId = Guid.NewGuid();
        _failureMemory
            .Setup(m => m.TryRecall(ExpectedKey))
            .Returns(new ApprovalFailureRecall(1, "timed out", FailureTextSubstitution.None, failureEscalationId));
        _failureMemory
            .Setup(m => m.TryRecallRevision(ExpectedKey))
            .Returns(new ApprovalRevisionRecall(2, "narrow the scope", revisionEscalationId));

        await Route(Config());

        Assert.NotNull(captured());
        Assert.Equal(revisionEscalationId, captured()!.PredecessorEscalationId);
    }

    [Fact]
    public async Task RequestApprovalAsync_MultipleReviseVotes_ComposeDeterministicallyByRespondedAt()
    {
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        var later = DateTimeOffset.UtcNow;
        var outcome = new EscalationOutcome
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = false,
            Decisions =
            [
                new ApproverDecision
                {
                    ApproverName = "bob", Verdict = ApproverVerdict.Revise,
                    Instructions = "second point", RespondedAt = later
                },
                new ApproverDecision
                {
                    ApproverName = "alice", Verdict = ApproverVerdict.Revise,
                    Instructions = "first point", RespondedAt = earlier
                }
            ],
            ResolutionType = EscalationResolutionType.Revised,
            ResolvedAt = DateTimeOffset.UtcNow,
            Approvers = ["alice", "bob"]
        };
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var result = await Route(
            WithRelayGate(enabled: true, approvers: ["alice", "bob"]), CreatePassthroughSanitizer().Object);

        Assert.NotNull(result.ModelFacingInstructions);
        var aliceIndex = result.ModelFacingInstructions!.IndexOf("first point", StringComparison.Ordinal);
        var bobIndex = result.ModelFacingInstructions.IndexOf("second point", StringComparison.Ordinal);
        Assert.True(aliceIndex >= 0 && bobIndex >= 0 && aliceIndex < bobIndex,
            "alice responded earlier and must appear first");
    }

    [Fact]
    public async Task RequestApprovalAsync_OffRosterDecision_IsExcludedFromTheRelay()
    {
        // A rehydrated outcome is not re-checked against a live roster the way a fresh submission
        // is — this is the defense-in-depth filter for that gap, on a channel that injects text
        // into model context.
        var outcome = new EscalationOutcome
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = false,
            Decisions =
            [
                new ApproverDecision
                {
                    ApproverName = "mallory", Verdict = ApproverVerdict.Revise,
                    Instructions = "do something bad", RespondedAt = DateTimeOffset.UtcNow
                }
            ],
            ResolutionType = EscalationResolutionType.Revised,
            ResolvedAt = DateTimeOffset.UtcNow,
            Approvers = ["alice"] // mallory is not on the roster this outcome carries
        };
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var result = await Route(WithRelayGate(enabled: true), CreatePassthroughSanitizer().Object);

        // The relay stays suppressed — mallory's text never reaches the model — but the round
        // still has to advance. If it didn't, an off-roster or revoked approver's vote would let
        // an action re-raise Revised forever without ever reaching MaxRounds, since BuildRequest
        // would keep recalling round 1 for this key on every subsequent attempt.
        Assert.Null(result.ModelFacingInstructions);
        _failureMemory.Verify(
            m => m.RecordRevision(ExpectedKey, 2, It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestApprovalAsync_ApproverRevokedFromLiveConfig_ExcludedEvenThoughStillOnTheRecordsRoster()
    {
        // The discriminating case for the live-config roster check: an approver present on
        // outcome.Approvers (the durable record's own frozen copy of the roster it was raised
        // under) but no longer in the operator's *current* configuration — e.g. revoked while this
        // escalation sat waiting on a human. Checking outcome.Decisions against outcome.Approvers
        // alone (both from the same record) would still accept this; checking against live config
        // must not.
        var outcome = new EscalationOutcome
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = false,
            Decisions =
            [
                new ApproverDecision
                {
                    ApproverName = "mallory", Verdict = ApproverVerdict.Revise,
                    Instructions = "do something bad", RespondedAt = DateTimeOffset.UtcNow
                }
            ],
            ResolutionType = EscalationResolutionType.Revised,
            ResolvedAt = DateTimeOffset.UtcNow,
            Approvers = ["alice", "mallory"] // mallory WAS on the roster this escalation was raised under
        };
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        // The live roster no longer includes mallory.
        var result = await Route(
            WithRelayGate(enabled: true, approvers: ["alice"]), CreatePassthroughSanitizer().Object);

        // Same reasoning as the off-roster test: the relay stays suppressed, but the round still
        // advances, or a revoked approver's vote would let this action re-raise Revised forever.
        Assert.Null(result.ModelFacingInstructions);
        _failureMemory.Verify(
            m => m.RecordRevision(ExpectedKey, 2, It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestApprovalAsync_NoRosterValidReviseDecision_StillAdvancesRoundWithPlaceholder()
    {
        // Direct regression test for the fix itself, isolated from any specific reason
        // ComposeRevisionFeedback returned null (off-roster, revoked-live-config, or genuinely no
        // Revise decisions at all) — round-tracking must not depend on why nothing was composable.
        var outcome = new EscalationOutcome
        {
            EscalationId = Guid.NewGuid(),
            IsApproved = false,
            Decisions = [],
            ResolutionType = EscalationResolutionType.Revised,
            ResolvedAt = DateTimeOffset.UtcNow,
            Approvers = ["alice"]
        };
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var result = await Route(WithRelayGate(enabled: true), CreatePassthroughSanitizer().Object);

        Assert.Null(result.ModelFacingInstructions);
        _failureMemory.Verify(
            m => m.RecordRevision(ExpectedKey, 2, It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestApprovalAsync_SanitizerRedactsToBlank_NeverRelaysButStillAdvancesTheRound()
    {
        // Round-tracking must not depend on the sanitizer leaving something usable behind — gating
        // RecordRevision on that would let a reviewer whose feedback keeps triggering redaction
        // revise forever without ever reaching MaxRounds. The model never sees a placeholder; the
        // round (and the next approver's card) still reflects that a revision happened.
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(SanitizationResult.WithFindings(string.Empty, "redacted everything", []));
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReviseOutcome());

        var result = await Route(WithRelayGate(enabled: true), sanitizer.Object);

        Assert.Null(result.ModelFacingInstructions);
        _failureMemory.Verify(
            m => m.RecordRevision(ExpectedKey, 2, It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestApprovalAsync_RelayText_ClampedToConfiguredMaxLength()
    {
        _escalation
            .Setup(x => x.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReviseOutcome(instructions: new string('x', 2000)));

        var result = await Route(
            WithRelayGate(enabled: true, maxRelayedLength: 50), CreatePassthroughSanitizer().Object);

        Assert.NotNull(result.ModelFacingInstructions);
        Assert.Contains("truncated", result.ModelFacingInstructions, StringComparison.Ordinal);
    }
}
