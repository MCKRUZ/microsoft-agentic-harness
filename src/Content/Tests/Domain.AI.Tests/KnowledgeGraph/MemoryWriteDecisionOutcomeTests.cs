using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.KnowledgeGraph;

/// <summary>
/// Tests for <see cref="MemoryWriteDecision.Outcome"/> — the tri-state projection the HTTP memory
/// surface reports to callers. The mapping must stay honest: a non-persisted decision is Rejected
/// no matter what trust it carries, and a persisted-untrusted decision is Quarantined.
/// </summary>
public sealed class MemoryWriteDecisionOutcomeTests
{
    [Fact]
    public void Outcome_PersistFalse_IsRejected_RegardlessOfTrust()
    {
        new MemoryWriteDecision { Persist = false, Trust = MemoryTrust.Untrusted, Reason = "rejected: Critical" }
            .Outcome.Should().Be(MemoryWriteOutcome.Rejected);

        // Trust is documented as ignored when Persist is false — the outcome must still be Rejected.
        new MemoryWriteDecision { Persist = false, Trust = MemoryTrust.Trusted, Reason = "rejected" }
            .Outcome.Should().Be(MemoryWriteOutcome.Rejected);
    }

    [Fact]
    public void Outcome_PersistUntrusted_IsQuarantined()
    {
        new MemoryWriteDecision { Persist = true, Trust = MemoryTrust.Untrusted, Reason = "quarantined: DirectOverride" }
            .Outcome.Should().Be(MemoryWriteOutcome.Quarantined);
    }

    [Fact]
    public void Outcome_PersistTrusted_IsPersisted()
    {
        new MemoryWriteDecision { Persist = true, Trust = MemoryTrust.Trusted, Reason = "trusted" }
            .Outcome.Should().Be(MemoryWriteOutcome.Persisted);
    }

    [Fact]
    public void Allow_ReportsPersistedOutcome()
    {
        MemoryWriteDecision.Allow().Outcome.Should().Be(MemoryWriteOutcome.Persisted);
    }
}
