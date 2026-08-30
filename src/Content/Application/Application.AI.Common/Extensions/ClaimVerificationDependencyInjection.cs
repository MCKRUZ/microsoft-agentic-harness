using Application.AI.Common.Interfaces.ClaimVerification;
using Application.AI.Common.Services.ClaimVerification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Registers the artifact-grounded claim-verification subsystem's core, judge-independent
/// components into DI. Composition root (Presentation) calls this from
/// <c>AddApplicationAIDependencies</c>; every host gets a resolvable
/// <see cref="ClaimVerificationRunner"/> regardless of whether it has opted into real LLM-backed
/// verification.
/// </summary>
/// <remarks>
/// <para>
/// Default-registered services:
/// <list type="bullet">
/// <item><see cref="IClaimConsequenceClassifier"/> → <see cref="RuleBasedClaimConsequenceClassifier"/>.</item>
/// <item><see cref="IClaimVerifier"/> → <see cref="NotConfiguredClaimVerifier"/> (fail-safe default,
/// not fail-fast — see that type's remarks for why this subsystem departs from the sibling
/// <c>NotConfiguredRolloutRunner</c>/<c>NotConfiguredPatchProposer</c> throw-on-use shape).</item>
/// <item><see cref="ClaimVerificationRunner"/> — concrete orchestrator, no interface seam.</item>
/// </list>
/// </para>
/// <para>
/// A host that wants real verification calls
/// <c>Infrastructure.AI.Evaluation.DependencyInjectionClaimVerification.AddClaimVerification</c>
/// AFTER this method — it registers <see cref="IClaimVerifier"/> via a plain <c>AddSingleton</c>,
/// which wins over this method's <c>TryAddSingleton</c> default by last-registration-wins, the same
/// mechanism <c>NotConfiguredEvalRunner</c>'s own remarks document for <c>IEvalRunner</c>. The two
/// <c>ILocatedArtifactReader</c> implementations are registered unconditionally in
/// <c>Infrastructure.AI</c> — they depend only on <c>IFileSystemService</c>/<c>IOptionsMonitor&lt;AppConfig&gt;</c>,
/// never on a judge model, so gating them behind the eval-only extension would be scope creep.
/// </para>
/// </remarks>
public static class ClaimVerificationDependencyInjection
{
    /// <summary>Registers the claim-verification subsystem's core services.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddClaimVerificationDependencies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClaimConsequenceClassifier, RuleBasedClaimConsequenceClassifier>();
        services.TryAddSingleton<IClaimVerifier, NotConfiguredClaimVerifier>();
        services.TryAddSingleton<ClaimVerificationRunner>();

        return services;
    }
}
