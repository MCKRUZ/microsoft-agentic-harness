using Domain.Common.Config.AI.BundleExecution;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="BundleExecutionConfig"/>, asserting that every bound quantity the section's
/// own contract declares "must be positive" actually is. The archive limits bound how much a hostile
/// bundle can cost before it is parsed; the TTL/interval knobs govern how long staged bundles, run
/// records, and unclaimed stream reservations survive; the concurrency cap bounds how many live
/// streams a single caller may drive.
/// </summary>
/// <remarks>
/// <para>
/// Every rule is unconditional (not gated on <see cref="BundleExecutionConfig.Enabled"/>): the class
/// defaults are all positive, so a host that omits the section — or leaves the subsystem off — binds
/// valid defaults and boots unchanged. A rule only bites when an operator supplies an explicit
/// non-positive value, which is a misconfiguration regardless of whether the feature is switched on.
/// </para>
/// <para>
/// The failure this closes is silent, not loud. A non-positive <see cref="BundleExecutionConfig.StreamReservationTtl"/>
/// makes a streaming reservation expire at or before it is created (<c>InMemoryBundleRunJobStore.Create</c>
/// seeds <c>ExpiresAt = now + StreamReservationTtl</c>), so the SSE feed is swept before the caller can
/// connect and the stream is silently unreachable. Likewise a non-positive <see cref="BundleExecutionConfig.CleanupInterval"/>
/// or TTL degrades the sweeper rather than crashing it. Failing closed at startup surfaces the operator's
/// mistake with a clear message instead of a subsystem that appears healthy but never streams.
/// </para>
/// </remarks>
public sealed class BundleExecutionConfigValidator : AbstractValidator<BundleExecutionConfig>
{
    /// <summary>
    /// Initializes the rule set. Bounds are inclusive-of-nothing (strictly positive) for every quantity,
    /// except <see cref="BundleExecutionConfig.MaxConcurrentStreamsPerCaller"/> where the meaningful floor
    /// is a single concurrent stream.
    /// </summary>
    public BundleExecutionConfigValidator()
    {
        RuleFor(x => x.MaxArchiveBytes)
            .GreaterThan(0)
            .WithMessage("MaxArchiveBytes must be > 0 — a non-positive limit would reject every archive.");

        RuleFor(x => x.MaxEntryCount)
            .GreaterThan(0)
            .WithMessage("MaxEntryCount must be > 0 — a non-positive limit would reject every archive.");

        RuleFor(x => x.MaxTotalUncompressedBytes)
            .GreaterThan(0)
            .WithMessage("MaxTotalUncompressedBytes must be > 0 — the decompression-bomb guard would reject every archive otherwise.");

        RuleFor(x => x.MaxCompressionRatio)
            .GreaterThan(0)
            .WithMessage("MaxCompressionRatio must be > 0 — the decompression-bomb ratio guard would reject every archive otherwise.");

        RuleFor(x => x.HandleTtl)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("HandleTtl must be > 0 — a non-positive lifetime expires every staged bundle immediately.");

        RuleFor(x => x.RunRecordTtl)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("RunRecordTtl must be > 0 — a non-positive lifetime evicts every completed run before it can be polled.");

        RuleFor(x => x.StreamReservationTtl)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("StreamReservationTtl must be > 0 — a non-positive lifetime sweeps every stream reservation before the caller can connect, silently disabling the SSE feed.");

        RuleFor(x => x.CleanupInterval)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("CleanupInterval must be > 0 — a non-positive interval breaks the background cleanup sweeper.");

        RuleFor(x => x.MaxConcurrentStreamsPerCaller)
            .GreaterThanOrEqualTo(1)
            .WithMessage("MaxConcurrentStreamsPerCaller must be >= 1 — a non-positive cap would deny every stream.");
    }
}
