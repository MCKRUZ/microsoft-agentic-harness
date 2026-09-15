using Application.AI.Common.Services.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using Domain.Common.Helpers;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="SandboxConfig"/> — specifically, that every configured
/// <see cref="ToolOverrideConfig.DeniedHosts"/>/<see cref="ToolOverrideConfig.AllowedHosts"/> entry
/// actually normalizes to something that can match a requested host (#647).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> <c>CapabilityEnforcer.EnforceHostScoping</c> already detects an
/// inert pattern — one that <see cref="SecureInputValidatorHelper.ValidateHost"/> would reject after
/// <see cref="HostPatternNormalizer.NormalizeHostForMatch"/> — via
/// <c>CapabilityEnforcer.WarnIfPatternIsInert</c>, a per-call log warning (#635 LOW). Code review on
/// that fix found two gaps in a per-call check: it only fires for a tool that is actually invoked with
/// a resolvable host, so a rarely-used tool's typo can sit undetected indefinitely; and it re-validates
/// every configured pattern on every single call for a tool that IS invoked often, for a fact that is
/// static for the life of the process. Expressing the identical check as a rule here runs it exactly
/// once, at process start, regardless of how often (or whether) any affected tool is ever called.
/// </para>
/// <para>
/// <strong>Reuses the runtime check's own normalization, not a re-implementation.</strong>
/// <see cref="HostPatternNormalizer"/> was extracted from <c>CapabilityEnforcer</c> specifically so
/// this validator and the runtime enforcement path share one normalization pipeline — a second,
/// independently-maintained copy of host-pattern normalization is exactly the kind of drift this
/// codebase has been bitten by before (two parsers of the same input silently disagreeing). If
/// <see cref="HostPatternNormalizer"/>'s rules ever change, this validator's verdict changes with it,
/// automatically.
/// </para>
/// <para>
/// <strong>Unconditional, not gated on <see cref="SandboxConfig.Enabled"/>.</strong> The same posture
/// <c>GovernanceConfigValidator</c> takes for <c>ToolBehaviorGating.Exemptions</c>: a malformed pattern
/// is a typo in the operator's config regardless of whether the sandbox subsystem happens to be on
/// today, and the config is read by a human long before the sandbox is.
/// </para>
/// <para>
/// <strong>Fails the host closed, not open.</strong> Every sibling validator wired through
/// <c>IServiceCollectionExtensions.RegisterValidatedConfigSections</c> uses <c>ValidateOnStart()</c>,
/// turning a bad value into a boot-time <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>
/// naming the exact tool, config key, and pattern — the same posture <c>BundleExecutionConfigValidator</c>
/// and <c>WorkflowSubmissionConfigValidator</c> already take for their own operator-set caps. The
/// alternative — leaving this as advisory-only — is the status quo #647 was filed to close: a
/// permanently inert deny/allow entry with no forcing function to fix it. A host with no
/// <c>ToolOverrides</c> configured (the default) sees no behavior change at all.
/// </para>
/// </remarks>
public sealed class SandboxConfigValidator : AbstractValidator<SandboxConfig>
{
    public SandboxConfigValidator()
    {
        RuleForEach(x => x.ToolOverrides)
            .ChildRules(entry =>
            {
                entry.RuleForEach(kvp => kvp.Value.DeniedHosts)
                    .Must(IsMatchableHostPatternOrNull)
                    .WithMessage((kvp, pattern) => BuildInertPatternMessage(kvp.Key, "DeniedHosts", pattern));

                entry.RuleForEach(kvp => kvp.Value.AllowedHosts)
                    .Must(IsMatchableHostPatternOrNull)
                    .WithMessage((kvp, pattern) => BuildInertPatternMessage(kvp.Key, "AllowedHosts", pattern));
            });
    }

    /// <summary>
    /// Mirrors <c>CapabilityEnforcer.WarnIfPatternIsInert</c>'s own check exactly: normalize, strip any
    /// wildcard prefix, then validate the bare host. A <see langword="null"/> entry (a literal JSON
    /// <c>null</c> in a config-bound <c>List&lt;string&gt;</c>, which bypasses the element type's
    /// non-nullability) is treated as valid here — the runtime path skips it the same way before it
    /// ever reaches normalization, rather than treating a binding artifact as an operator typo.
    /// </summary>
    private static bool IsMatchableHostPatternOrNull(string? pattern)
    {
        if (pattern is null)
            return true;

        var normalized = HostPatternNormalizer.NormalizeHostForMatch(pattern);
        var checkValue = HostPatternNormalizer.HasWildcardPrefix(normalized) ? normalized[2..] : normalized;
        return SecureInputValidatorHelper.ValidateHost(checkValue);
    }

    private static string BuildInertPatternMessage(string toolName, string configKey, string? pattern) =>
        $"Sandbox.ToolOverrides['{toolName}'].{configKey} contains an entry that normalizes to an " +
        $"invalid host and can never match any requested host: '{pattern}'. Fix or remove it — as " +
        "configured, this entry is a permanent, silent no-op.";
}
