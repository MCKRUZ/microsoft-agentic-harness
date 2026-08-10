using Domain.AI.Governance;
using Xunit;

namespace Domain.AI.Tests.Governance;

/// <summary>
/// The exemption rule: which declarations are strong enough to let a tool skip human approval.
/// </summary>
/// <remarks>
/// This is the whole security argument of behaviour-based gating in one property, so it is tested
/// exhaustively rather than by example. The case that matters most is the one an attacker controls: a
/// read-only claim from a server nobody vouched for must buy nothing.
/// </remarks>
public sealed class ToolBehaviorTests
{
    [Fact]
    public void Unknown_IsNotExempt()
    {
        // Silence is not a claim. Every path that fails to establish what a tool does lands here, so
        // this is the answer that decides whether the posture is fail-closed.
        Assert.False(ToolBehavior.Unknown.IsExemptFromApproval);
        Assert.Equal("nothing is known about what the tool does", ToolBehavior.Unknown.NonExemptReason);
    }

    [Theory]
    [InlineData(ToolBehaviorSource.FirstParty)]
    [InlineData(ToolBehaviorSource.TrustedMcpServer)]
    public void ReadOnly_FromATrustedSource_IsExempt(ToolBehaviorSource source)
    {
        var behavior = new ToolBehavior(source, ReadOnly: true);

        Assert.True(behavior.IsExemptFromApproval);
        Assert.Null(behavior.NonExemptReason);
    }

    [Fact]
    public void ReadOnly_FromAnUntrustedServer_IsNotExempt()
    {
        // The control the whole design turns on. A server that wants to escape the gate marks its
        // destructive tool read-only; the MCP specification says so in as many words. If this ever
        // returns true, the posture protects nothing against the party it exists to police.
        var behavior = new ToolBehavior(ToolBehaviorSource.UntrustedMcpServer, ReadOnly: true);

        Assert.False(behavior.IsExemptFromApproval);
        Assert.Equal(
            "the tool claims to be read-only, but its MCP server is not marked as trusted for tool annotations",
            behavior.NonExemptReason);
    }

    [Theory]
    [InlineData(ToolBehaviorSource.FirstParty)]
    [InlineData(ToolBehaviorSource.TrustedMcpServer)]
    [InlineData(ToolBehaviorSource.UntrustedMcpServer)]
    public void Destructive_OutranksAReadOnlyClaim_FromEverySource(ToolBehaviorSource source)
    {
        // An incoherent declaration — only reads, yet destroys — is a reason to distrust the declarer,
        // not an invitation to pick the convenient half. Tightening is believed from anyone.
        var behavior = new ToolBehavior(source, ReadOnly: true, Destructive: true);

        Assert.False(behavior.IsExemptFromApproval);
        Assert.Equal("the tool declares itself destructive", behavior.NonExemptReason);
    }

    [Theory]
    [InlineData(ToolBehaviorSource.FirstParty)]
    [InlineData(ToolBehaviorSource.TrustedMcpServer)]
    [InlineData(ToolBehaviorSource.UntrustedMcpServer)]
    public void NotReadOnly_IsNeverExempt_HoweverTrustedTheSource(ToolBehaviorSource source)
    {
        var declared = new ToolBehavior(source, ReadOnly: false);
        var silent = new ToolBehavior(source);

        Assert.False(declared.IsExemptFromApproval);
        Assert.False(silent.IsExemptFromApproval);
        Assert.Equal("the tool has not declared itself read-only", declared.NonExemptReason);
        Assert.Equal("the tool has not declared itself read-only", silent.NonExemptReason);
    }

    [Fact]
    public void TheOtherTwoHints_AreRecorded_AndDoNotDecideExemption()
    {
        // Idempotency and open-world reach are worth keeping — they are what a later report or a
        // risk-tier rule reads — but neither answers "does this change anything", so neither may move
        // the gate on its own. A tool that writes is gated however idempotent it claims to be.
        var idempotentWriter = new ToolBehavior(
            ToolBehaviorSource.TrustedMcpServer, ReadOnly: false, Idempotent: true, OpenWorld: false);

        Assert.False(idempotentWriter.IsExemptFromApproval);
        Assert.True(idempotentWriter.Idempotent);
        Assert.False(idempotentWriter.OpenWorld);

        var openWorldReader = new ToolBehavior(
            ToolBehaviorSource.TrustedMcpServer, ReadOnly: true, OpenWorld: true);

        Assert.True(openWorldReader.IsExemptFromApproval);
    }
}
