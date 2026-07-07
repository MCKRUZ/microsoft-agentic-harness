using Domain.Common.Config.AI.HarmonicMemory;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="HarmonicMemoryConfig"/>: the content-length floor and recall cue-anchor fan-out must
/// be non-negative, and the consolidation top-K and recall RRF constant must be positive. Auto-discovered via
/// <c>AddValidatorsFromAssembly</c>, consistent with the sibling config validators
/// (<c>WorkMemoryConfigValidator</c> et al.).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HarmonicMemoryConfig.Mode"/> needs no rule — the enum's values are exhaustive and
/// <see cref="HarmonicMemoryMode.Off"/> is a valid default. <see cref="HarmonicMemoryConfig.ConsolidationTopK"/>
/// is only consulted in <see cref="HarmonicMemoryMode.Full"/>, but is validated unconditionally so a
/// misconfiguration is caught before the mode is ever raised.
/// </para>
/// <para>
/// <see cref="HarmonicMemoryConfig.BatchAtSessionFlush"/> is rejected when <see langword="true"/>: the
/// write path abstracts inline on each <c>RememberAsync</c>, and there is no session-flush seam to defer
/// into — cross-session memory is persisted durably inline, and <c>ISessionKnowledgeCache.FlushToGraphAsync</c>
/// has no production caller for the memory path. Rather than silently ignore the flag (a lying knob), the
/// validator fails loud at startup; deferred batching is a scoped future change that will lift this rule.
/// </para>
/// </remarks>
public sealed class HarmonicMemoryConfigValidator : AbstractValidator<HarmonicMemoryConfig>
{
    /// <summary>Initializes a new instance of the <see cref="HarmonicMemoryConfigValidator"/> class.</summary>
    public HarmonicMemoryConfigValidator()
    {
        RuleFor(x => x.MinContentLengthChars)
            .GreaterThanOrEqualTo(0).WithMessage("MinContentLengthChars must be >= 0.");

        RuleFor(x => x.ConsolidationTopK)
            .GreaterThan(0).WithMessage("ConsolidationTopK must be > 0.");

        RuleFor(x => x.RecallCueAnchorFanout)
            .GreaterThanOrEqualTo(0).WithMessage("RecallCueAnchorFanout must be >= 0.");

        RuleFor(x => x.RecallRrfK)
            .GreaterThan(0).WithMessage("RecallRrfK must be > 0.");

        RuleFor(x => x.BatchAtSessionFlush)
            .Equal(false)
            .WithMessage(
                "BatchAtSessionFlush is not supported in this build: harmonic abstraction runs inline on " +
                "each RememberAsync, and there is no session-flush seam to defer into. Leave it false; a " +
                "future release will add deferred batching.");
    }
}
