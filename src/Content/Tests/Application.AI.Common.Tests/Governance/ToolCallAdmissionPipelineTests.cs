using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Domain.AI.Changes;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Tests for the one composed admission chain every execution path runs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The ordering test is the load-bearing one here.</strong> Before this type existed, the
/// order was maintained by hand at five separate call sites and asserted piecemeal — several separate
/// "X was not consulted when Y denied" tests, none of which pinned the whole sequence. A stage
/// inserted in the wrong place could satisfy every one of them.
/// <see cref="AdmitAsync_RunsEveryStageInTheOneOrderThatIsSafe"/> records the actual sequence and
/// compares it to the intended one, so any reordering fails one assertion with a readable diff.
/// </para>
/// <para>
/// Two orderings are safety properties rather than preferences. The host's own rules run <em>last of
/// the access gates</em>, so consumer-authored code can only ever tighten an outcome the built-in
/// gates already permitted. And the loop guard runs <em>last of all</em>, because it is the only stage
/// that mutates: asking it about a call is also what records that call, and counting a call that was
/// then blocked let an agent reset the no-progress counter on every retry and spin indefinitely
/// against the very rule blocking it.
/// </para>
/// <para>
/// The guard's coupling is not a defect to be refactored away — see
/// <c>ProgressEvaluatorConcurrencyTests</c>, which is the control showing that splitting it admits a
/// whole parallel batch. This position is what makes the coupling safe.
/// </para>
/// </remarks>
public sealed class ToolCallAdmissionPipelineTests
{
    private const string Tool = "file_system";

    private static readonly IReadOnlyDictionary<string, object?> Args =
        new Dictionary<string, object?> { ["path"] = "/etc/passwd" };

    [Fact]
    public async Task AdmitAsync_RunsEveryStageInTheOneOrderThatIsSafe()
    {
        var order = new List<string>();
        var pipeline = Recording(order);

        await pipeline.AdmitAsync(
            new ToolCallAdmissionRequest(Tool, Args, CountsTowardLoopDetection: true), CancellationToken.None);

        order.Should().Equal(
            ["agent-authorization", "governor", "classification", "host-rules", "loop-guard"],
            "agent RBAC runs first because it is the cheapest and most fundamental access question, "
            + "and because the governor can escalate to a human — nobody should be asked to approve a "
            + "call that RBAC refuses anyway; permission and policy then settle whether the agent may "
            + "use the tool at all; the host's own rules run after them so they can only tighten; and "
            + "the loop guard runs last because asking it is also what records the call, so it must "
            + "only ever be asked about calls that reached the tool");
    }

    [Theory]
    [InlineData("agent-authorization")]
    [InlineData("governor")]
    [InlineData("classification")]
    [InlineData("host-rules")]
    [InlineData("loop-guard")]
    public async Task AdmitAsync_AStageRefuses_NothingAfterItRuns(string refusingStage)
    {
        var order = new List<string>();
        var pipeline = Recording(order, refusingStage);

        var admission = await pipeline.AdmitAsync(
            new ToolCallAdmissionRequest(Tool, Args, CountsTowardLoopDetection: true), CancellationToken.None);

        admission.IsAllowed.Should().BeFalse();
        order.Should().Equal(order.TakeWhile(s => s != refusingStage).Append(refusingStage));
    }

    [Fact]
    public async Task AdmitAsync_AuthorizationRefuses_TheRefusalIsRecordedOnTheTrace()
    {
        // The authorization stage runs ahead of the governor, so the governor never sees a call this
        // stage refuses and records nothing for it. Without an explicit record, a turn in which an
        // agent was refused every tool it attempted reports zero denials to governance reporting,
        // the dashboard and the audit — which for an access-control decision is indistinguishable
        // from not having enforced it.
        var trace = new Mock<IGovernanceTraceRecorder>();

        var pipeline = AdmissionHarness.Pipeline(
            trace: trace.Object,
            authorizationGate: AdmissionHarness.DenyingAuthorizationGate("nope").Object);

        var admission = await pipeline.AdmitAsync(
            new ToolCallAdmissionRequest(Tool, Args), CancellationToken.None);

        admission.IsAllowed.Should().BeFalse();
        trace.Verify(
            t => t.RecordDownstreamBlock(Tool, It.Is<string>(r => r.Contains("authorization"))),
            Times.Once);
    }

    [Fact]
    public async Task AdmitAsync_LoopDetectionNotRequested_TheGuardIsNotConsulted()
    {
        // Every caller but the agent turn. A single Execution API call has no sequence to evaluate,
        // and a plan DAG has its own retry and recovery — a guard that could only ever see one call is
        // machinery that cannot fire, or can only misfire.
        var progress = new Mock<IProgressEvaluator>(MockBehavior.Strict);

        var admission = await AdmissionHarness
            .Pipeline(progressEvaluator: progress.Object)
            .AdmitAsync(new ToolCallAdmissionRequest(Tool, Args), CancellationToken.None);

        admission.IsAllowed.Should().BeTrue();
        progress.Verify(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()), Times.Never);
    }

    [Fact]
    public async Task AdmitAsync_NoHostRulesRegistered_TheChainIsNotConsulted()
    {
        // The default composition registers the chain and no rules in it. Skipping it outright rather
        // than calling an empty chain is what keeps the default path free of a per-call dictionary
        // allocation and a virtual call.
        var observers = new Mock<IToolCallObserverChain>(MockBehavior.Strict);
        observers.SetupGet(o => o.HasObservers).Returns(false);

        await AdmissionHarness
            .Pipeline(observers: observers.Object)
            .AdmitAsync(new ToolCallAdmissionRequest(Tool, Args), CancellationToken.None);

        observers.Verify(
            o => o.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AdmitAsync_NoArguments_AHostRuleStillReceivesADictionary()
    {
        // A rule reads arguments to make an argument-sensitive decision. Handing it null where the
        // caller had none would make every consumer-authored rule carry a null check, and the one that
        // forgot would throw — which the chain fails closed on, turning "no arguments" into a refusal.
        IReadOnlyDictionary<string, object?>? seen = null;
        var observers = new Mock<IToolCallObserverChain>();
        observers.SetupGet(o => o.HasObservers).Returns(true);
        observers
            .Setup(o => o.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, object?>, CancellationToken>((_, a, _) => seen = a)
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));

        await AdmissionHarness
            .Pipeline(observers: observers.Object)
            .AdmitAsync(new ToolCallAdmissionRequest(Tool), CancellationToken.None);

        seen.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task AdmitAsync_NoArguments_TheGovernorStillSeesTheAbsence()
    {
        // The opposite of the rule above, and deliberately so: the governor distinguishes "the caller
        // had no arguments to give" from "the call had none", and narrows its argument-conditioned
        // policy rules accordingly. Substituting an empty dictionary here would silently widen them.
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .ReturnsAsync(ToolInvocationDecision.Allow());

        await AdmissionHarness
            .Pipeline(governor: governor.Object)
            .AdmitAsync(new ToolCallAdmissionRequest(Tool), CancellationToken.None);

        governor.Verify(g => g.AuthorizeAsync(Tool, It.IsAny<CancellationToken>(), null), Times.Once);
    }

    [Fact]
    public async Task AdmitAsync_NoArguments_ClassificationIsNotConsulted()
    {
        // A request with no arguments is a capability gate — "may this run call a model at all" — and
        // has no data surface. Running the gate would not classify anything: it would resolve to
        // Unknown and hand the decision to the host's unknown-asset policy, a verdict about the
        // absence of information rather than about the call. A host that hardened that policy to
        // Block would otherwise fail every LLM-call and retrieval step in every plan.
        var gate = new Mock<IToolClassificationGate>(MockBehavior.Strict);

        var admission = await AdmissionHarness
            .Pipeline(classificationGate: gate.Object)
            .AdmitAsync(new ToolCallAdmissionRequest("llm_call"), CancellationToken.None);

        admission.IsAllowed.Should().BeTrue();
        gate.Verify(
            g => g.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AdmitAsync_EmptyArguments_ClassificationStillRuns()
    {
        // The other half of the rule above, and the reason it is safe: a tool call always carries an
        // argument dictionary even when it is empty, so "no arguments at all" cleanly separates a
        // capability gate from a tool call. A zero-argument tool that reads a fixed classified file
        // must still be classified.
        var gate = new Mock<IToolClassificationGate>();
        gate
            .Setup(g => g.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClassificationVerdict.Block("restricted"));

        var admission = await AdmissionHarness
            .Pipeline(classificationGate: gate.Object)
            .AdmitAsync(
                new ToolCallAdmissionRequest(Tool, new Dictionary<string, object?>()), CancellationToken.None);

        admission.IsAllowed.Should().BeFalse("a tool call with an empty argument set is still a tool call");
    }

    [Fact]
    public async Task AdmitAsync_RedactVerdict_AllowsTheCallAndMarksTheOutput()
    {
        var gate = new Mock<IToolClassificationGate>();
        gate
            .Setup(g => g.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClassificationVerdict.RedactOutput());
        gate.Setup(g => g.RedactResult(Tool, It.IsAny<object?>())).Returns("[redacted]");

        var pipeline = AdmissionHarness.Pipeline(classificationGate: gate.Object);
        var admission = await pipeline.AdmitAsync(new ToolCallAdmissionRequest(Tool, Args), CancellationToken.None);

        admission.IsAllowed.Should().BeTrue("a redact verdict lets the call run — it scrubs the answer");
        admission.RedactsOutput.Should().BeTrue();
        pipeline.ApplyOutputPolicy(admission, Tool, "secret").Should().Be("[redacted]");
    }

    [Fact]
    public void ApplyOutputPolicy_PlainAllow_ReturnsTheResultUntouchedWithoutCallingTheGate()
    {
        var gate = new Mock<IToolClassificationGate>(MockBehavior.Strict);
        var pipeline = AdmissionHarness.Pipeline(classificationGate: gate.Object);

        pipeline.ApplyOutputPolicy(ToolCallAdmission.Allow(), Tool, "plain").Should().Be("plain");

        gate.Verify(g => g.RedactResult(It.IsAny<string>(), It.IsAny<object?>()), Times.Never);
    }

    [Fact]
    public async Task AdmitAsync_AStageRefusesWithNoMessage_TheCallerStillGetsTheCanonicalRefusal()
    {
        // Defensive: every stage's own refusal factory requires a message, so this is unreachable
        // today. It resolves to the canonical text rather than something naming the stage, because a
        // refused caller must not be able to tell which gate stopped them from the message alone.
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .ReturnsAsync(new ToolInvocationDecision(false, null));

        var admission = await AdmissionHarness
            .Pipeline(governor: governor.Object)
            .AdmitAsync(new ToolCallAdmissionRequest(Tool, Args), CancellationToken.None);

        admission.IsAllowed.Should().BeFalse();
        admission.DeniedMessage.Should().Be(GovernanceDenials.NotPermitted(Tool));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deny_RefusesARefusalWithNothingToSay(string? message)
    {
        // Callers act on DeniedMessage directly — the agent turn returns it to the model in place of
        // the tool result — so a blank refusal reaches the model as an empty successful result, which
        // it reads as the tool having run and returned nothing. There is no public constructor, so
        // this factory is the only way to build a refusal and therefore the only place to enforce it.
        var act = () => ToolCallAdmission.Deny(message!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reset_ClearsEveryStatefulPartOfTheChain()
    {
        // These were reset independently at each arming site, and a site that reset one but not the
        // other carried a turn's history into the next. One call now covers both — the shared
        // governance trail, and the loop guard's own call history, which is the only per-turn state
        // that does not live on the trail.
        var trace = AdmissionHarness.TraceRecorder();
        trace.Record(new ToolDecisionRecord(Tool, ToolDecisionOutcome.Denied, "nope", BlastRadius.Low,
            RequiredApproval: false, ApprovalGranted: false, Enforced: true));
        trace.RecordEscalation("progress.spin_detected");
        var progress = new Mock<IProgressEvaluator>();

        AdmissionHarness.Pipeline(progressEvaluator: progress.Object, trace: trace).Reset();

        trace.Snapshot().Should().BeSameAs(GovernanceTrace.Empty);
        progress.Verify(p => p.Reset(), Times.Once);
    }

    [Fact]
    public void GetTrace_ReportsWhatEveryStageRecorded_AsOneRecord()
    {
        // The governor's decisions and the loop guard's escalation codes arrive at the trail
        // independently, and the turn wants them as one record. This used to be composed by hand here
        // — and, before the chain existed, by hand at two separate callers.
        var trace = AdmissionHarness.TraceRecorder();
        trace.Record(new ToolDecisionRecord(Tool, ToolDecisionOutcome.Denied, "policy", BlastRadius.Low,
            RequiredApproval: false, ApprovalGranted: false, Enforced: true));
        trace.RecordEscalation("progress.spin_detected");
        trace.RecordEscalation("PROGRESS.SPIN_DETECTED");

        var snapshot = AdmissionHarness.Pipeline(trace: trace).GetTrace();

        snapshot.ToolDecisions.Should().ContainSingle().Which.Reason.Should().Be("policy");
        snapshot.EscalationReasonCodes.Should().BeEquivalentTo(
            ["progress.spin_detected"],
            "codes are unioned case-insensitively to honour the trace's distinct contract");
    }

    [Fact]
    public void GetTrace_NothingRecordedAndNothingEnforced_IsTheEmptyTrace()
    {
        AdmissionHarness.Pipeline().GetTrace().Should().BeSameAs(GovernanceTrace.Empty);
    }

    /// <summary>
    /// Builds the chain over gates that record the order they ran in, with one optionally refusing.
    /// </summary>
    /// <param name="order">Collects each stage's label as it runs.</param>
    /// <param name="refusingStage">The stage that refuses, or null when every stage permits.</param>
    private static ToolCallAdmissionPipeline Recording(List<string> order, string? refusingStage = null)
    {
        var authorizationGate = new Mock<IAgentToolAuthorizationGate>();
        authorizationGate
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("agent-authorization"))
            .ReturnsAsync(refusingStage == "agent-authorization"
                ? ToolInvocationDecision.Deny("no")
                : ToolInvocationDecision.Allow());

        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Callback(() => order.Add("governor"))
            .ReturnsAsync(refusingStage == "governor"
                ? ToolInvocationDecision.Deny("no")
                : ToolInvocationDecision.Allow());

        var classificationGate = new Mock<IToolClassificationGate>();
        classificationGate
            .Setup(g => g.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("classification"))
            .ReturnsAsync(refusingStage == "classification"
                ? ClassificationVerdict.Block("no")
                : ClassificationVerdict.Allow());

        var observers = new Mock<IToolCallObserverChain>();
        observers.SetupGet(o => o.HasObservers).Returns(true);
        observers
            .Setup(o => o.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("host-rules"))
            .Returns(ValueTask.FromResult(refusingStage == "host-rules"
                ? ToolInvocationDecision.Deny("no")
                : ToolInvocationDecision.Allow()));

        var progress = new Mock<IProgressEvaluator>();
        progress
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Callback(() => order.Add("loop-guard"))
            .Returns(refusingStage == "loop-guard"
                ? ProgressVerdict.Halt("no")
                : ProgressVerdict.Continue());

        return new ToolCallAdmissionPipeline(
            authorizationGate.Object, governor.Object, classificationGate.Object, observers.Object,
            progress.Object, AdmissionHarness.TraceRecorder(),
            new Mock<IApprovalExecutionReporter>().Object,
            NullLogger<ToolCallAdmissionPipeline>.Instance);
    }

    // ===== ReportExecutionAsync: #325 execution reporting dispatch =====

    private static ToolCallAdmissionPipeline WithReporter(Mock<IApprovalExecutionReporter> reporter) => new(
        Mock.Of<IAgentToolAuthorizationGate>(), Mock.Of<IToolInvocationGovernor>(),
        Mock.Of<IToolClassificationGate>(), Mock.Of<IToolCallObserverChain>(), Mock.Of<IProgressEvaluator>(),
        AdmissionHarness.TraceRecorder(), reporter.Object, NullLogger<ToolCallAdmissionPipeline>.Instance);

    private static ApprovedCall Call() =>
        new(Guid.NewGuid(), new ApprovalFailureKey("conv-1", "agent-1", Tool));

    [Fact]
    public async Task ReportExecutionAsync_NoApprovedCall_IsANoOp()
    {
        // Most calls need no human approval — nothing to report a loop closing on.
        var reporter = new Mock<IApprovalExecutionReporter>();
        var admission = ToolCallAdmission.Allow();

        await WithReporter(reporter).ReportExecutionAsync(
            admission, new ToolExecutionReport(EscalationExecutionStatus.Succeeded, null, null), "test-site", CancellationToken.None);

        reporter.Verify(r => r.ReportSucceededAsync(It.IsAny<ApprovedCall>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        reporter.Verify(r => r.ReportFailedAsync(It.IsAny<ApprovedCall>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        reporter.Verify(r => r.ReportNotExecutedAsync(It.IsAny<ApprovedCall>(), It.IsAny<EscalationNotExecutedReason>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReportExecutionAsync_Succeeded_DispatchesToReportSucceededAsync()
    {
        var reporter = new Mock<IApprovalExecutionReporter>();
        var call = Call();
        var admission = ToolCallAdmission.Allow().WithApproval(call);

        await WithReporter(reporter).ReportExecutionAsync(
            admission, new ToolExecutionReport(EscalationExecutionStatus.Succeeded, null, null), "test-site", CancellationToken.None);

        reporter.Verify(r => r.ReportSucceededAsync(call, It.IsAny<string>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ReportExecutionAsync_FailedWithReason_DispatchesToReportFailedAsync()
    {
        var reporter = new Mock<IApprovalExecutionReporter>();
        var call = Call();
        var admission = ToolCallAdmission.Allow().WithApproval(call);

        await WithReporter(reporter).ReportExecutionAsync(
            admission, new ToolExecutionReport(EscalationExecutionStatus.Failed, "permission denied", null), "test-site", CancellationToken.None);

        reporter.Verify(r => r.ReportFailedAsync(call, "permission denied", It.IsAny<string>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ReportExecutionAsync_FailedWithNoReason_IsANoOp()
    {
        // An incoherent report (Failed with no reason) is a caller bug, not something to guess at —
        // this is the one place in the reporting path with no must-not-throw contract protecting it,
        // so it must not throw either; it simply reports nothing.
        var reporter = new Mock<IApprovalExecutionReporter>();
        var admission = ToolCallAdmission.Allow().WithApproval(Call());

        await WithReporter(reporter).ReportExecutionAsync(
            admission, new ToolExecutionReport(EscalationExecutionStatus.Failed, null, null), "test-site", CancellationToken.None);

        reporter.Verify(r => r.ReportFailedAsync(It.IsAny<ApprovedCall>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReportExecutionAsync_NeverExecutedWithReason_DispatchesToReportNotExecutedAsync()
    {
        var reporter = new Mock<IApprovalExecutionReporter>();
        var call = Call();
        var admission = ToolCallAdmission.Allow().WithApproval(call);

        await WithReporter(reporter).ReportExecutionAsync(
            admission,
            new ToolExecutionReport(EscalationExecutionStatus.NeverExecuted, null, EscalationNotExecutedReason.RunCancelled),
            "test-site", CancellationToken.None);

        reporter.Verify(
            r => r.ReportNotExecutedAsync(call, EscalationNotExecutedReason.RunCancelled, It.IsAny<string>(), CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task ReportExecutionAsync_NeverExecutedWithNoReason_IsANoOp()
    {
        var reporter = new Mock<IApprovalExecutionReporter>();
        var admission = ToolCallAdmission.Allow().WithApproval(Call());

        await WithReporter(reporter).ReportExecutionAsync(
            admission, new ToolExecutionReport(EscalationExecutionStatus.NeverExecuted, null, null), "test-site", CancellationToken.None);

        reporter.Verify(
            r => r.ReportNotExecutedAsync(It.IsAny<ApprovedCall>(), It.IsAny<EscalationNotExecutedReason>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
