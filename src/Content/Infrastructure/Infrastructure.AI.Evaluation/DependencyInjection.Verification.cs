using Application.AI.Common.Interfaces.Verification;
using Application.AI.Common.Services.Verification;
using Infrastructure.AI.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Evaluation;

/// <summary>
/// <see cref="IObligationExtractor"/>/<see cref="IObligationVerifier"/> DI registration, factored
/// out for the same reason as <see cref="DependencyInjectionJudges.AddLlmJudge(IServiceCollection)"/>:
/// a consumer that needs obligation-based analysis without the rest of the eval framework (dataset
/// loaders, eval metrics, reporters, <c>IEvalRunner</c>) can call it alone.
/// </summary>
/// <remarks>
/// Lives here rather than in <c>Infrastructure.AI</c> — where the concrete
/// <see cref="LlmObligationExtractor"/>/<see cref="LlmObligationVerifier"/> implementations are —
/// because both depend on <see cref="Application.AI.Common.Evaluation.Interfaces.IJudgeChatClientProvider"/>,
/// which <see cref="DependencyInjectionJudges.AddLlmJudge(IServiceCollection)"/> registers, and
/// <c>Infrastructure.AI</c> does not (and must not) reference <c>Infrastructure.AI.Evaluation</c> —
/// the dependency runs the other way. <c>DependencyInjection.AddEvaluationDependencies</c> calls
/// this automatically for the common case.
/// </remarks>
public static class DependencyInjectionVerification
{
    /// <summary>
    /// Registers <see cref="IObligationExtractor"/>, <see cref="IObligationVerifier"/>,
    /// <see cref="ObligationVerificationRunner"/>, and their shared judge-model client provider
    /// (via <see cref="DependencyInjectionJudges.AddLlmJudge(IServiceCollection)"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddObligationVerification(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLlmJudge();
        services.AddSingleton<ObligationValidator>();
        services.AddSingleton<IObligationExtractor, LlmObligationExtractor>();
        services.AddSingleton<IObligationVerifier, LlmObligationVerifier>();
        services.AddSingleton<ObligationVerificationRunner>();

        return services;
    }
}
