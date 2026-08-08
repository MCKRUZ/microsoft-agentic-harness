using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Domain.AI.Governance;
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
/// that mutates: it records the call's signature, and counting a call that was then blocked let an
/// agent reset the no-progress counter on every retry and spin indefinitely against the very rule
/// blocking it.
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
            ["governor", "classification", "host-rules", "loop-guard"],
            "permission and policy settle whether the agent may use the tool at all; the host's own "
            + "rules run after them so they can only tighten; and the loop guard runs last because it "
            + "is the only stage that records state, so it must count only calls that reached the tool");
    }

    [Theory]
    [InlineData("governor")]
    [InlineData("classification")]
    [InlineData("host-rules")]
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

    [Fact]
    public void Reset_ClearsEveryStatefulStage_NotJustTheGovernor()
    {
        // These were reset independently at each arming site, and a site that reset one but not the
        // other carried a turn's history into the next. One call now covers both.
        var governor = new Mock<IToolInvocationGovernor>();
        var progress = new Mock<IProgressEvaluator>();

        AdmissionHarness.Pipeline(governor: governor.Object, progressEvaluator: progress.Object).Reset();

        governor.Verify(g => g.Reset(), Times.Once);
        progress.Verify(p => p.Reset(), Times.Once);
    }

    [Fact]
    public void GetTrace_FoldsTheLoopGuardsEscalationsIntoTheGovernorsTrace()
    {
        var governor = new Mock<IToolInvocationGovernor>();
        governor.Setup(g => g.GetTrace()).Returns(new GovernanceTrace { EscalationReasonCodes = ["from_governor"] });
        var progress = new Mock<IProgressEvaluator>();
        progress.SetupGet(p => p.EscalationReasonCodes).Returns(["spin_detected", "FROM_GOVERNOR"]);

        var trace = AdmissionHarness
            .Pipeline(governor: governor.Object, progressEvaluator: progress.Object)
            .GetTrace();

        trace.EscalationReasonCodes.Should().BeEquivalentTo(
            ["from_governor", "spin_detected"],
            "codes are unioned case-insensitively to honour the trace's distinct contract");
    }

    [Fact]
    public void GetTrace_NoEscalations_ReturnsTheGovernorsTraceUnchanged()
    {
        var expected = new GovernanceTrace { EnforcementEnabled = true };
        var governor = new Mock<IToolInvocationGovernor>();
        governor.Setup(g => g.GetTrace()).Returns(expected);
        var progress = new Mock<IProgressEvaluator>();
        progress.SetupGet(p => p.EscalationReasonCodes).Returns([]);

        AdmissionHarness
            .Pipeline(governor: governor.Object, progressEvaluator: progress.Object)
            .GetTrace()
            .Should().BeSameAs(expected);
    }

    /// <summary>
    /// Builds the chain over gates that record the order they ran in, with one optionally refusing.
    /// </summary>
    /// <param name="order">Collects each stage's label as it runs.</param>
    /// <param name="refusingStage">The stage that refuses, or null when every stage permits.</param>
    private static ToolCallAdmissionPipeline Recording(List<string> order, string? refusingStage = null)
    {
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
            .Returns(ProgressVerdict.Continue());

        return new ToolCallAdmissionPipeline(
            governor.Object, classificationGate.Object, observers.Object, progress.Object);
    }
}
