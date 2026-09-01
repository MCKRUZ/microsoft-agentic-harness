using Domain.Common.Config.AI.ContextManagement;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ToolResultStorageConfig"/> — the large-tool-result persistence settings
/// bound from <c>AppConfig:AI:ContextManagement:ToolResultStorage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Wired into the options pipeline with <c>ValidateOnStart()</c> in the composition root, so an
/// invalid section fails the host at boot rather than at first use.
/// </para>
/// <para>
/// <strong>Why this section needs a validator now (#532).</strong>
/// <see cref="ToolResultStorageConfig.PerResultCharLimit"/> used to decide only whether a large
/// result was spilled to disk and previewed, so a nonsensical value degraded storage behaviour and
/// nothing else. It is now also the ceiling
/// <c>ToolCallAdmissionPipeline</c> cuts every tool result to before the model sees it. At zero or
/// below, that cut takes <em>everything</em>: each result reaches the model as an empty string, the
/// agent behaves as though every tool it calls returns nothing, and no error is raised anywhere.
/// A setting that was merely suboptimal became silently destructive, which is exactly the class of
/// misconfiguration that should stop a host from booting rather than be discovered from behaviour.
/// </para>
/// <para>
/// The two cross-field rules encode coherence the class cannot express on its own. A preview larger
/// than the limit that triggers previewing, or an aggregate per-message budget smaller than what a
/// single result may occupy, are not tunings — they are contradictions, and both would otherwise be
/// accepted and then quietly resolved in whichever direction the reading code happens to check
/// first. The shipped defaults (50,000 / 200,000 / 2,000) satisfy every rule, so a host that omits
/// the section — which is every host today — keeps booting unchanged.
/// </para>
/// </remarks>
public sealed class ToolResultStorageConfigValidator : AbstractValidator<ToolResultStorageConfig>
{
    /// <summary>Initializes a new instance of the <see cref="ToolResultStorageConfigValidator"/> class.</summary>
    public ToolResultStorageConfigValidator()
    {
        RuleFor(x => x.PerResultCharLimit)
            .GreaterThan(0)
            .WithMessage(
                "ToolResultStorage.PerResultCharLimit must be greater than zero — it is the ceiling "
                + "every tool result is cut to before the model sees it, so a non-positive value "
                + "silently replaces every tool result with an empty string.");

        RuleFor(x => x.PreviewSizeChars)
            .GreaterThan(0)
            .WithMessage("ToolResultStorage.PreviewSizeChars must be greater than zero.");

        RuleFor(x => x.PreviewSizeChars)
            .LessThanOrEqualTo(x => x.PerResultCharLimit)
            .WithMessage(
                "ToolResultStorage.PreviewSizeChars must not exceed PerResultCharLimit — the preview "
                + "is what is kept in context when a result is too large to keep inline, so a preview "
                + "bigger than that threshold is a contradiction.");

        RuleFor(x => x.AggregatePerMessageCharLimit)
            .GreaterThanOrEqualTo(x => x.PerResultCharLimit)
            .WithMessage(
                "ToolResultStorage.AggregatePerMessageCharLimit must be at least PerResultCharLimit — "
                + "an aggregate budget smaller than what one result may occupy cannot be satisfied by "
                + "a single result.");

        RuleFor(x => x.StoragePath)
            .NotEmpty()
            .WithMessage("ToolResultStorage.StoragePath must not be empty.");

        RuleFor(x => x.MaxSpillChars)
            .GreaterThan(0)
            .WithMessage("ToolResultStorage.MaxSpillChars must be greater than zero.");

        RuleFor(x => x.MaxSpillChars)
            .GreaterThanOrEqualTo(x => x.PerResultCharLimit)
            .WithMessage(
                "ToolResultStorage.MaxSpillChars must be at least PerResultCharLimit — a spill cap "
                + "smaller than the ceiling that triggers spilling would refuse to persist the very "
                + "results it exists to make retrievable.");
    }
}
