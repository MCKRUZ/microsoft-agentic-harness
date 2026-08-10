using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Bundles;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the turn's governance trail on its own — which is the point of it having been pulled out
/// of <c>ToolInvocationGovernor</c>. Every assertion here used to require constructing a governor and
/// its twelve dependencies, and driving a tool call through it, to observe a list.
/// </summary>
public sealed class GovernanceTraceRecorderTests
{
    private const string Tool = "file_system";

    private static GovernanceTraceRecorder Create(bool enforceGlobally = false) =>
        AdmissionHarness.TraceRecorder(new GovernanceConfig { EnforceToolInvocation = enforceGlobally });

    private static ToolDecisionRecord Decision(string reason, ToolDecisionOutcome outcome = ToolDecisionOutcome.Allowed) =>
        new(Tool, outcome, reason, BlastRadius.Low,
            RequiredApproval: false, ApprovalGranted: false, Enforced: true);

    [Fact]
    public void Snapshot_UngovernedAndNothingRecorded_IsTheSharedEmptyTrace()
    {
        // By reference. Callers distinguish "this turn was never governed and nothing happened" from
        // "nothing was allowed" by identity, and the default composition never enforces, so this is
        // overwhelmingly the common answer.
        Create().Snapshot().Should().BeSameAs(GovernanceTrace.Empty);
    }

    [Fact]
    public void Snapshot_KeepsDecisionsInTheOrderTheyWereRecorded()
    {
        // The trace reads as a narrative of the turn to an auditor; a set would lose the sequence in
        // which the agent attempted things.
        var recorder = Create();

        recorder.Record(Decision("first"));
        recorder.Record(Decision("second", ToolDecisionOutcome.Denied));
        recorder.Record(Decision("third"));

        recorder.Snapshot().ToolDecisions.Select(d => d.Reason)
            .Should().Equal("first", "second", "third");
    }

    [Fact]
    public void Snapshot_IsACopy_NotALiveViewOfTheTrail()
    {
        // A caller holding a snapshot must not see a later tool call appear inside it. The turn
        // handler puts this on the turn result and the trail keeps running until the scope ends.
        var recorder = Create();
        recorder.Record(Decision("first"));

        var snapshot = recorder.Snapshot();
        recorder.Record(Decision("second"));

        snapshot.ToolDecisions.Should().ContainSingle();
    }

    [Fact]
    public void RecordEscalation_DeduplicatesCaseInsensitively()
    {
        // GovernanceTrace.EscalationReasonCodes is contractually distinct, and GovernanceTrace.Merge
        // unions codes case-insensitively when folding per-turn traces into a conversation. A trail
        // that deduplicated case-sensitively would produce a turn trace the merge then collapsed,
        // so the same conversation would report different counts depending on where you read it.
        var recorder = Create();

        recorder.RecordEscalation("progress.spin_detected");
        recorder.RecordEscalation("PROGRESS.SPIN_DETECTED");
        recorder.RecordEscalation("escalation.timeout");

        recorder.Snapshot().EscalationReasonCodes
            .Should().BeEquivalentTo(["progress.spin_detected", "escalation.timeout"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordEscalation_BlankCode_IsRejected(string code)
    {
        // A code nothing can be keyed off is noise on an audit trail, and worse than absent: it makes
        // a turn look escalated to anything counting codes.
        var recorder = Create();

        recorder.Invoking(r => r.RecordEscalation(code)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Snapshot_EscalationsButNoDecisions_IsNotTheEmptyTrace()
    {
        // The loop guard can raise an escalation for a call it refused before the governor recorded
        // anything. Collapsing that to Empty would lose the only evidence the turn was escalated.
        var recorder = Create();

        recorder.RecordEscalation("progress.spin_detected");

        var snapshot = recorder.Snapshot();
        snapshot.Should().NotBeSameAs(GovernanceTrace.Empty);
        snapshot.EscalationReasonCodes.Should().ContainSingle();
    }

    [Fact]
    public void EnforcementEnabled_GlobalOptInOn_IsTrueWithoutAnyToolCall()
    {
        // A turn under global enforcement that made no tool calls is still a governed turn. Reporting
        // it as ungoverned would tell governance reporting that enforcement was off in exactly the
        // turns where nothing needed stopping.
        var recorder = Create(enforceGlobally: true);

        recorder.EnforcementEnabled.Should().BeTrue();
        recorder.Snapshot().EnforcementEnabled.Should().BeTrue();
    }

    [Fact]
    public void EnforcementEnabled_BundleEnvelopeArmed_TracksTheAmbientScope()
    {
        var recorder = Create();

        using (CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope { AllowedTools = [Tool] }))
            recorder.EnforcementEnabled.Should().BeTrue("a bundle run is always governed");

        recorder.EnforcementEnabled.Should().BeFalse("the run ended and nothing was authorized under it");
    }

    [Fact]
    public void MarkEnforced_SurvivesTheAmbientEnvelopeTearingDown()
    {
        // The trace is assembled after the run, by which time the ambient envelope is long gone. A
        // turn that authorized under enforcement is still an enforced turn.
        var recorder = Create();

        using (CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope { AllowedTools = [Tool] }))
            recorder.MarkEnforced();

        recorder.EnforcementEnabled.Should().BeTrue();
        recorder.Snapshot().EnforcementEnabled.Should().BeTrue();
    }

    [Fact]
    public void Reset_ClearsDecisionsEscalationsAndTheEnforcedFlag()
    {
        // Nested MediatR sends within a conversation share one DI scope and therefore one trail, so a
        // turn that did not clear it would double-count when per-turn traces are merged.
        var recorder = Create();
        recorder.Record(Decision("first"));
        recorder.RecordEscalation("progress.spin_detected");
        recorder.MarkEnforced();

        recorder.Reset();

        recorder.EnforcementEnabled.Should().BeFalse();
        recorder.Snapshot().Should().BeSameAs(GovernanceTrace.Empty);
    }

    [Fact]
    public void Record_NullDecision_IsRejected()
    {
        Create().Invoking(r => r.Record(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RecordDownstreamBlock_Enforced_ClassifiesViaTheInjectedRiskClassifier()
    {
        // The whole point of moving this method off the governor was that the recorder can resolve a
        // tool's blast radius on its own, from the same singleton classifier the governor uses, rather
        // than needing one supplied by a caller. This is the only test that would fail if the wiring
        // regressed to a hardcoded radius instead of an actual classifier call.
        var classifier = new Mock<IToolRiskClassifier>();
        classifier.Setup(c => c.Classify(Tool)).Returns(new ToolRiskProfile(BlastRadius.Critical, IsReadOnly: false));
        var monitor = Mock.Of<IOptionsMonitor<GovernanceConfig>>(
            m => m.CurrentValue == new GovernanceConfig { EnforceToolInvocation = true });
        var recorder = new GovernanceTraceRecorder(monitor, classifier.Object);

        recorder.RecordDownstreamBlock(Tool, "blocked by observer 'wire-limit'");

        var record = recorder.Snapshot().ToolDecisions.Should().ContainSingle().Subject;
        record.BlastRadius.Should().Be(BlastRadius.Critical);
        classifier.Verify(c => c.Classify(Tool), Times.Once);
    }

    [Fact]
    public void RecordDownstreamBlock_Ungoverned_NeverConsultsTheRiskClassifier()
    {
        // The enforcement check is meant to short-circuit before classification runs, not just before
        // the record is written — an ungoverned turn should pay nothing for a block nobody will read.
        var classifier = new Mock<IToolRiskClassifier>();
        var recorder = new GovernanceTraceRecorder(
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(
                m => m.CurrentValue == new GovernanceConfig { EnforceToolInvocation = false }),
            classifier.Object);

        recorder.RecordDownstreamBlock(Tool, "blocked by observer 'wire-limit'");

        classifier.Verify(c => c.Classify(It.IsAny<string>()), Times.Never);
        recorder.Snapshot().ToolDecisions.Should().BeEmpty(
            "there is no earlier decision to correct and nothing governance-relevant to add");
    }
}

/// <summary>
/// Verifies the single shared answer to "is per-invocation governance on for this flow?" — the switch
/// the governor reads to decide whether to engage and the trail reads to decide whether the turn was
/// governed. It is one predicate precisely so those two can never disagree.
/// </summary>
public sealed class GovernanceEnforcementTests
{
    private static GovernanceConfig Config(bool enforceGlobally) =>
        new() { EnforceToolInvocation = enforceGlobally };

    [Fact]
    public void IsActive_DefaultComposition_IsFalse()
    {
        // The shipped default. Governance is opt-in, and off it the governor is a pure pass-through.
        GovernanceEnforcement.IsActive(Config(enforceGlobally: false)).Should().BeFalse();
    }

    [Fact]
    public void IsActive_GlobalOptIn_IsTrue()
    {
        GovernanceEnforcement.IsActive(Config(enforceGlobally: true)).Should().BeTrue();
    }

    [Fact]
    public void IsActive_InsideABundleRun_IsTrueEvenWithTheGlobalSwitchOff()
    {
        // A bundle executes an externally-authored agent, so its whole flow must be governed. The
        // presence of a per-caller envelope is the single fact this derives from, which means there is
        // no way to publish an envelope without also arming the governor.
        var config = Config(enforceGlobally: false);

        using var armed = CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope { AllowedTools = ["t"] });

        GovernanceEnforcement.IsActive(config).Should().BeTrue();
    }

    [Fact]
    public void IsActive_IsLive_NotSticky()
    {
        // Load-bearing: this is what the governor reads to decide whether to enforce, so a bundle's
        // enforcement must end with its run. The sticky form — "was this turn ever governed?" — lives
        // on the trail instead, and using it here would keep enforcement armed after the run ended.
        var config = Config(enforceGlobally: false);

        using (CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope { AllowedTools = ["t"] }))
            GovernanceEnforcement.IsActive(config).Should().BeTrue();

        GovernanceEnforcement.IsActive(config).Should().BeFalse();
    }
}
