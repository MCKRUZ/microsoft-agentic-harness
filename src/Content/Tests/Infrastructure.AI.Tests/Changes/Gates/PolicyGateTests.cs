using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using FluentAssertions;
using Infrastructure.AI.Changes.Gates;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GateAction = Domain.AI.Changes.GateAction;

namespace Infrastructure.AI.Tests.Changes.Gates;

public sealed class PolicyGateTests
{
    private sealed class ScriptedPolicy(string key, params PolicyFinding[] findings) : IChangeProposalPolicy
    {
        public string Key { get; } = key;
        public Task<IReadOnlyList<PolicyFinding>> EvaluateAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PolicyFinding>>(findings);
    }

    private sealed class ThrowingPolicy(string key) : IChangeProposalPolicy
    {
        public string Key { get; } = key;
        public Task<IReadOnlyList<PolicyFinding>> EvaluateAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("policy exploded");
    }

    private static PolicyFinding Finding(string policyKey, PolicyFindingSeverity sev, string msg = "issue") => new()
    {
        PolicyKey = policyKey,
        Severity = sev,
        Message = msg
    };

    private static (PolicyGate Gate, string TempDir) Build(params IChangeProposalPolicy[] policies)
        => Build(blockingSeverity: null, policies);

    private static (PolicyGate Gate, string TempDir) Build(
        string? blockingSeverity,
        params IChangeProposalPolicy[] policies)
    {
        var (monitor, dir) = TestConfig.NewMonitor();
        if (blockingSeverity is not null)
        {
            monitor.CurrentValue.AI.Changes.PolicyBlockingSeverity = blockingSeverity;
        }

        return (new PolicyGate(policies, monitor, NullLogger<PolicyGate>.Instance), dir);
    }

    private static GateContext Ctx() => new()
    {
        Mode = OrchestratorMode.Live,
        AttemptCount = 1,
        EvaluatedAt = TestProposals.DefaultTime,
        CorrelationId = "corr-1"
    };

    [Fact]
    public async Task EvaluateAsync_NoPoliciesRegistered_FailsWithDirectiveMessage()
    {
        var (sut, dir) = Build();
        try
        {
            var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

            result.Action.Should().Be(GateAction.Fail);
            result.Reason.Should().Contain("No IChangeProposalPolicy is registered");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EvaluateAsync_NoFindings_ReturnsPass()
    {
        var (sut, dir) = Build(new ScriptedPolicy("checkov"));
        try
        {
            var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

            result.Action.Should().Be(GateAction.Pass);
            result.Reason.Should().Contain("no findings");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EvaluateAsync_FindingsBelowThreshold_ReturnsPass()
    {
        // Default blocking threshold is High; Low + Medium should not block.
        var (sut, dir) = Build(
            new ScriptedPolicy("checkov",
                Finding("checkov", PolicyFindingSeverity.Low, "minor"),
                Finding("checkov", PolicyFindingSeverity.Medium, "moderate")));
        try
        {
            var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

            result.Action.Should().Be(GateAction.Pass);
            result.Reason.Should().Contain("below");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EvaluateAsync_HighSeverityFinding_FailsAtDefaultThreshold()
    {
        var (sut, dir) = Build(new ScriptedPolicy("checkov", Finding("checkov", PolicyFindingSeverity.High, "public S3")));
        try
        {
            var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

            result.Action.Should().Be(GateAction.Fail);
            result.Reason.Should().Contain("public S3");
            result.Reason.Should().Contain("checkov");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EvaluateAsync_CriticalSeverityFinding_AlwaysFails()
    {
        var (sut, dir) = Build(new ScriptedPolicy("opa", Finding("opa", PolicyFindingSeverity.Critical, "missing tag")));
        try
        {
            var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

            result.Action.Should().Be(GateAction.Fail);
            result.Reason.Should().Contain("Critical");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EvaluateAsync_AggregatesFindingsAcrossPolicies()
    {
        var (sut, dir) = Build(
            new ScriptedPolicy("checkov", Finding("checkov", PolicyFindingSeverity.Low, "x")),
            new ScriptedPolicy("opa", Finding("opa", PolicyFindingSeverity.High, "y")));
        try
        {
            var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

            result.Action.Should().Be(GateAction.Fail);
            // High from opa drives the block; reason mentions opa.
            result.Reason.Should().Contain("opa");
            result.Reason.Should().Contain("y");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("99")]                  // outside the defined range
    [InlineData(" 99")]                 // and the same thing behind a stray space
    [InlineData("Low,Critical")]        // comma-composite: OR'd to Critical(4), a defined member
    public async Task EvaluateAsync_UnparseableBlockingSeverity_FallsBackToHighAndStillBlocks(string configured)
    {
        // #300. The threshold drives `finding.Severity >= threshold`, so a permissive parse is not a
        // cosmetic issue: Enum.TryParse accepts "99", yields a PolicyFindingSeverity of 99, and every
        // real severity compares below it. The gate then reports "evaluated, N finding(s) below
        // threshold" and blocks nothing — the exact failure #296 measured on the approval path.
        // Refusing the value means the documented strict default (High) actually takes effect.
        //
        // Mutation-checked: reverting ParseThreshold to Enum.TryParse fails every row here. Note the
        // rows are deliberately not "3": High is 3 AND High is the fallback, so the numeric form of
        // that particular member cannot distinguish the two implementations. The numeric-form case
        // that CAN discriminate is the test below.
        var (sut, dir) = Build(
            configured,
            new ScriptedPolicy("opa", Finding("opa", PolicyFindingSeverity.Critical, "public bucket")));
        try
        {
            var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

            result.Action.Should().Be(GateAction.Fail);
            result.Reason.Should().Contain("public bucket");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EvaluateAsync_NumericFormOfARealSeverity_IsIgnoredInFavourOfTheDefault()
    {
        // "1" is Low, so a permissive parse would lower the threshold and block this Medium finding.
        // Name-only refuses it and the High default stands, so the finding passes. This is the
        // deliberate cost of the contract — a number and a name are never interchangeable, because a
        // value that means one thing to a boot validator and another to a runtime parser is what
        // #296 was. An operator wanting Low writes "Low".
        var (sut, dir) = Build(
            "1",
            new ScriptedPolicy("checkov", Finding("checkov", PolicyFindingSeverity.Medium, "moderate")));
        try
        {
            var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

            result.Action.Should().Be(GateAction.Pass);
            result.Reason.Should().Contain("below");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EvaluateAsync_NamedBlockingSeverity_IsStillHonoured()
    {
        // The control for the theory above: rejecting non-name forms must not mean ignoring the
        // setting. A named threshold below the default still raises what blocks.
        var (sut, dir) = Build(
            nameof(PolicyFindingSeverity.Low),
            new ScriptedPolicy("checkov", Finding("checkov", PolicyFindingSeverity.Low, "nit")));
        try
        {
            var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

            result.Action.Should().Be(GateAction.Fail);
            result.Reason.Should().Contain("nit");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EvaluateAsync_PolicyThrows_ReturnsFailNotThrow()
    {
        var (sut, dir) = Build(new ThrowingPolicy("checkov"));
        try
        {
            var result = await sut.EvaluateAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

            result.Action.Should().Be(GateAction.Fail);
            result.Reason.Should().Contain("InvalidOperationException");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
