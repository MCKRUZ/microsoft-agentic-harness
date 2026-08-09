using Application.AI.Common.Services.Governance;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Pins the loop guard's behaviour when tool calls arrive <strong>at the same time</strong>, which is
/// how the agent actually issues them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this file exists.</strong> The guard both answers "is this a repeat?" and records the
/// call, in one locked step. That coupling reads as poor separation of concerns and was refactored
/// away — answer now, record once the call is admitted — with every other test still green, because
/// every other test calls the guard one call at a time. In a batch it was broken: the agent factory
/// sets <c>AllowConcurrentInvocation</c>, so an assistant message's tool calls run in parallel against
/// the one turn-scoped guard, and with the two halves separated they all asked before any of them had
/// been recorded. Every one was told it was the first. The whole batch ran.
/// </para>
/// <para>
/// <strong>Why it repeats the batch rather than running one.</strong> A single batch is not evidence.
/// A split guard passes a one-shot batch whenever the threads happen not to interleave, and measuring
/// that is not optional — the first version of this file did exactly one batch, and a deliberately
/// re-split evaluator passed it twice in a row. Repeating the batch turns a scheduling coincidence
/// into a vanishing one. The assertion is safe in the other direction by construction: an atomic guard
/// cannot admit a second identical call however the threads are scheduled, so more iterations can only
/// ever expose a real defect, never invent one.
/// </para>
/// </remarks>
public sealed class ProgressEvaluatorConcurrencyTests
{
    private const int Batch = 8;
    private const int Rounds = 200;

    private static ProgressEvaluator Create(ProgressGuardConfig guard) =>
        new(Mock.Of<IOptionsMonitor<GovernanceConfig>>(m =>
                m.CurrentValue == new GovernanceConfig { ProgressGuard = guard }),
            AdmissionHarness.TraceRecorder(),
            NullLogger<ProgressEvaluator>.Instance);

    private static ProgressGuardConfig Guard() => new()
    {
        Enabled = true,
        RepetitionThreshold = 2,
        NoProgressWindow = 100
    };

    /// <summary>
    /// Fires <see cref="Batch"/> identical calls simultaneously, <see cref="Rounds"/> times over, and
    /// reports the largest number admitted in any one round.
    /// </summary>
    /// <remarks>
    /// The same threads are reused across rounds and re-synchronised on a multi-phase barrier, whose
    /// between-rounds action banks the previous round's tally and clears the guard's history. That is
    /// much faster than standing up a batch per round, and it keeps every round's threads genuinely
    /// released together rather than trickling in from the thread pool.
    /// </remarks>
    private static async Task<int> WorstRoundAsync(ProgressEvaluator evaluator, Func<int, string> signatureForRound)
    {
        var admitted = 0;
        var worst = 0;
        var round = 0;

        using var betweenRounds = new Barrier(Batch, _ =>
        {
            worst = Math.Max(worst, Volatile.Read(ref admitted));
            Volatile.Write(ref admitted, 0);
            evaluator.Reset();
            round++;
        });

        await Task.WhenAll(Enumerable.Range(0, Batch).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                // Phase boundary: banks the previous round and clears the guard, then releases all
                // Batch threads into the same round together.
                betweenRounds.SignalAndWait();
                var signature = signatureForRound(Volatile.Read(ref round));
                if (!evaluator.Evaluate("read", () => signature).ShouldHalt)
                    Interlocked.Increment(ref admitted);
            }

            // One last phase so the final round is banked like the others.
            betweenRounds.SignalAndWait();
        })));

        return worst;
    }

    [Fact]
    public async Task Evaluate_ParallelIdenticalCalls_AdmitsOnlyTheFirst()
    {
        // The model emitted the same call eight times in one message. With a repetition threshold of
        // two, exactly one may run: the second identical call is already a repeat.
        var evaluator = Create(Guard());

        (await WorstRoundAsync(evaluator, _ => "x")).Should().Be(1,
            "answering and recording happen in one critical section, so a batch of identical calls "
            + "serialises and each one sees the calls before it");
    }

    [Fact]
    public async Task Evaluate_ParallelIdenticalCalls_LeavesTheCountersConsistentWithWhatRan()
    {
        // The counters must reflect the batch, not lag behind it: a guard that admitted one call but
        // recorded eight would then refuse a genuinely different call afterwards.
        var evaluator = Create(Guard());

        await WorstRoundAsync(evaluator, _ => "x");
        evaluator.Reset();

        evaluator.Evaluate("read", () => "x").ShouldHalt.Should().BeFalse("a cleared turn starts fresh");
        evaluator.Evaluate("read", () => "x").ShouldHalt.Should().BeTrue("still the same repeated call");
        evaluator.Evaluate("read", () => "y").ShouldHalt.Should().BeFalse(
            "a genuinely new call must release the agent, however many times the old one was tried");
    }

    [Fact]
    public async Task Evaluate_ParallelDistinctCalls_AdmitsThemAll()
    {
        // The other direction, and the one that matters for false positives: eight different calls in
        // one batch are eight pieces of new information, and the guard must not touch any of them. A
        // guard that over-counted under contention would cut off an agent that is working fine, which
        // is a worse failure than catching a spin late.
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = 2,
            NoProgressWindow = 2
        });

        using var releaseTogether = new Barrier(Batch);
        var admitted = 0;

        await Task.WhenAll(Enumerable.Range(0, Batch).Select(i => Task.Run(() =>
        {
            releaseTogether.SignalAndWait();
            if (!evaluator.Evaluate("read", () => $"arg-{i}").ShouldHalt)
                Interlocked.Increment(ref admitted);
        })));

        admitted.Should().Be(Batch, "every call in the batch introduced a new signature");
    }
}
