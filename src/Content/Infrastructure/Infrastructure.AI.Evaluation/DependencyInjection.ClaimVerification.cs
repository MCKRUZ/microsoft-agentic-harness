using Application.AI.Common.Interfaces.ClaimVerification;
using Infrastructure.AI.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Evaluation;

/// <summary>
/// <see cref="IClaimVerifier"/> real-implementation DI registration, factored out for the same
/// reason as <see cref="DependencyInjectionVerification.AddObligationVerification(IServiceCollection)"/>:
/// a consumer that needs claim verification without the rest of the eval framework can call it
/// alone.
/// </summary>
/// <remarks>
/// <para>
/// Lives here rather than in <c>Infrastructure.AI</c> — where <see cref="LlmClaimVerifier"/> and
/// the two <c>ILocatedArtifactReader</c> implementations are — because
/// <see cref="LlmClaimVerifier"/> depends on
/// <see cref="Application.AI.Common.Evaluation.Interfaces.IJudgeChatClientProvider"/>, which only
/// <see cref="DependencyInjectionJudges.AddLlmJudge(IServiceCollection)"/> registers, and
/// <c>Infrastructure.AI</c> does not (and must not) reference <c>Infrastructure.AI.Evaluation</c> —
/// the dependency runs the other way. <c>DependencyInjection.AddEvaluationDependencies</c> calls
/// this automatically for the common case.
/// </para>
/// <para>
/// Registers <see cref="IClaimVerifier"/> via a plain <c>AddSingleton</c>, not <c>TryAddSingleton</c>
/// — it must WIN over the <c>NotConfiguredClaimVerifier</c> fail-safe default every host registers
/// via <c>ClaimVerificationDependencyInjection.AddClaimVerificationDependencies</c>, by
/// last-registration-wins, so a host must call this method AFTER the core registration (the normal
/// composition order — <c>AddApplicationAIDependencies</c> before <c>AddEvaluationDependencies</c>).
/// </para>
/// </remarks>
public static class DependencyInjectionClaimVerification
{
    /// <summary>
    /// Registers the real <see cref="LlmClaimVerifier"/> as <see cref="IClaimVerifier"/> (overriding
    /// the fail-safe default) and its shared judge-model client provider (via
    /// <see cref="DependencyInjectionJudges.AddLlmJudge(IServiceCollection)"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddClaimVerification(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLlmJudge();
        services.AddSingleton<IClaimVerifier, LlmClaimVerifier>();

        return services;
    }
}
