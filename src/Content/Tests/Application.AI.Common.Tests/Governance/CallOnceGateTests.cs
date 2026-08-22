using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Tests for <see cref="CallOnceGate"/>: the policy short-circuit for a tool that was never
/// declared call-once, the deliberate fail-open when the execution carries no call-once scope,
/// and the mapping from a ledger claim to an allow/deny verdict.
/// </summary>
public sealed class CallOnceGateTests
{
    private const string Tool = "start_diagnostic_session";
    private const string ScopeId = "scope-1";

    private static Mock<IToolCallOncePolicy> DeclaredCallOnce(bool isCallOnce)
    {
        var policy = new Mock<IToolCallOncePolicy>();
        policy.Setup(p => p.IsCallOnce(Tool)).Returns(isCallOnce);
        return policy;
    }

    private static Mock<IAgentExecutionContext> ExecutionContext(string? callOnceScopeId)
    {
        var context = new Mock<IAgentExecutionContext>();
        context.SetupGet(c => c.CallOnceScopeId).Returns(callOnceScopeId);
        return context;
    }

    private static CallOnceGate Gate(
        Mock<IToolCallOncePolicy> policy, Mock<IToolCallLedger>? ledger, Mock<IAgentExecutionContext> context) =>
        new(policy.Object, context.Object, NullLogger<CallOnceGate>.Instance, ledger?.Object);

    [Fact]
    public async Task EvaluateAsync_ToolNotDeclaredCallOnce_AllowsWithoutTouchingTheLedger()
    {
        var policy = DeclaredCallOnce(isCallOnce: false);
        var ledger = new Mock<IToolCallLedger>(MockBehavior.Strict);
        var context = ExecutionContext(ScopeId);

        var decision = await Gate(policy, ledger, context).EvaluateAsync(Tool, CancellationToken.None);

        decision.IsAllowed.Should().BeTrue();
        ledger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EvaluateAsync_LedgerNotComposed_AllowsForACallOnceTool()
    {
        // Infrastructure.AI's governance-state registration is what provides IToolCallLedger — a
        // host composing only Application.AI.Common (most test fixtures; a template consumer who
        // has not yet wired durable governance state) has none. This must behave as
        // NullToolCallLedger would, not fail the whole admission chain — see CallOnceGate's remarks.
        var policy = DeclaredCallOnce(isCallOnce: true);
        var context = ExecutionContext(ScopeId);

        var decision = await Gate(policy, ledger: null, context).EvaluateAsync(Tool, CancellationToken.None);

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_NoCallOnceScope_AllowsWithoutTouchingTheLedger()
    {
        // Deliberate fail-open — see CallOnceGate's remarks on why this is not the same mistake
        // as reading an absent identity as "global" elsewhere in this codebase: there is no
        // conversation for a second call to happen within, so there is nothing to protect.
        var policy = DeclaredCallOnce(isCallOnce: true);
        var ledger = new Mock<IToolCallLedger>(MockBehavior.Strict);
        var context = ExecutionContext(callOnceScopeId: null);

        var decision = await Gate(policy, ledger, context).EvaluateAsync(Tool, CancellationToken.None);

        decision.IsAllowed.Should().BeTrue();
        ledger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EvaluateAsync_ConversationIdSetButCallOnceScopeIdNot_StillAllows()
    {
        // The regression this guards against: CallOnceGate must read CallOnceScopeId, never
        // ConversationId directly. ConversationId means three different things to three different
        // callers (see IAgentExecutionContext.CallOnceScopeId's remarks) — a gate that fell back to
        // it would silently re-enable the exact cross-tenant denial-of-service and
        // direct-invoke-unenforceable bugs CallOnceScopeId exists to close.
        var policy = DeclaredCallOnce(isCallOnce: true);
        var ledger = new Mock<IToolCallLedger>(MockBehavior.Strict);
        var context = new Mock<IAgentExecutionContext>();
        context.SetupGet(c => c.ConversationId).Returns("conv-1");
        context.SetupGet(c => c.CallOnceScopeId).Returns((string?)null);

        var decision = await Gate(policy, ledger, context).EvaluateAsync(Tool, CancellationToken.None);

        decision.IsAllowed.Should().BeTrue();
        ledger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EvaluateAsync_LedgerClaimSucceeds_Allows()
    {
        var policy = DeclaredCallOnce(isCallOnce: true);
        var ledger = new Mock<IToolCallLedger>();
        ledger.Setup(l => l.TryClaimAsync(ScopeId, Tool, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var context = ExecutionContext(ScopeId);

        var decision = await Gate(policy, ledger, context).EvaluateAsync(Tool, CancellationToken.None);

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_LedgerClaimFails_DeniesWithAnActionableMessage()
    {
        var policy = DeclaredCallOnce(isCallOnce: true);
        var ledger = new Mock<IToolCallLedger>();
        ledger.Setup(l => l.TryClaimAsync(ScopeId, Tool, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var context = ExecutionContext(ScopeId);

        var decision = await Gate(policy, ledger, context).EvaluateAsync(Tool, CancellationToken.None);

        decision.IsAllowed.Should().BeFalse();
        // Specific enough for the model to act on — not the generic access-control denial every
        // other gate in the chain uses (GovernanceDenials.NotPermitted) — see CallOnceGate's
        // remarks. Deliberately does NOT assert the message claims "already been called": a
        // TryClaimAsync false can also mean the claim could not be durably recorded (a write
        // failure), and the message is worded to stay true under either cause.
        decision.DeniedMessage.Should().Contain(Tool).And.Contain("cannot be called again");
    }
}
