using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Prompts.Interfaces;
using Domain.Common.Config;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Presentation.Common.Startup;

/// <summary>
/// Fail-fast guard that resolves a curated set of critical options bindings and services
/// at host start, throwing a single aggregated error if any required wiring is missing.
/// </summary>
/// <remarks>
/// <para>
/// This is the structural defense against the "built-but-never-wired" defect class: a type
/// ships with full surface, docs, and tests but is never registered or bound, so it is
/// silently inert at runtime. The compiler cannot catch this and unit tests that mock the
/// dependency pass anyway. This guard converts those latent gaps into a loud failure the
/// first time the host boots.
/// </para>
/// <para>
/// Two resolution contexts are exercised: singleton/options resolutions run against the root
/// provider, and scoped resolutions run inside a freshly created scope (resolving a scoped
/// service from the root provider throws under scope validation, so the scope is mandatory).
/// Each check is isolated; every failure is collected so a single boot reports the complete
/// list of broken registrations rather than failing on the first one.
/// </para>
/// <para>
/// Registered last in the composition root so every other registration has already run.
/// </para>
/// </remarks>
public sealed class StartupRegistrationSmokeCheck : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StartupRegistrationSmokeCheck> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupRegistrationSmokeCheck"/> class.
    /// </summary>
    /// <param name="serviceProvider">The root service provider to validate against.</param>
    /// <param name="logger">Logger for the validation outcome.</param>
    public StartupRegistrationSmokeCheck(
        IServiceProvider serviceProvider,
        ILogger<StartupRegistrationSmokeCheck> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs every smoke check and throws an aggregated <see cref="InvalidOperationException"/>
    /// when any required binding or registration is missing.
    /// </summary>
    /// <param name="cancellationToken">Token to observe while starting.</param>
    /// <returns>A completed task when all checks pass.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when one or more critical options bindings or services cannot be resolved.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        // Options bindings: resolving CurrentValue forces the bind to run. A missing
        // Configure<T>/AddOptions<T> registration throws OptionsValidationException or
        // returns a type that never materialized. SandboxConfig in particular was the
        // canonical "built-but-never-bound" case this guard exists to catch.
        CheckOptions<AppConfig>(failures, "AppConfig");
        CheckOptions<SandboxConfig>(failures, "SandboxConfig (capability enforcement)");

        // Singleton services consumed by the agent execution pipeline.
        CheckRoot<IAgentFactory>(failures, "IAgentFactory");
        CheckRoot<IToolChainBuilder>(failures, "IToolChainBuilder");
        CheckRoot<IPromptRegistry>(failures, "IPromptRegistry");

        // Scoped services must be resolved inside a scope, never from the root provider.
        using (var scope = _serviceProvider.CreateScope())
        {
            CheckScoped<ICapabilityEnforcer>(scope, failures, "ICapabilityEnforcer");
        }

        if (failures.Count > 0)
        {
            var detail = string.Join("; ", failures);
            // Full detail is logged here; the thrown message intentionally lists the same
            // service names (no secrets/exception internals) so the boot failure is actionable.
            _logger.LogCritical(
                "Startup registration smoke check failed: {FailureCount} critical binding(s)/service(s) could not be resolved: {Failures}",
                failures.Count, detail);

            throw new InvalidOperationException(
                $"Startup registration smoke check failed for {failures.Count} critical binding(s)/service(s): {detail}. " +
                "This indicates a missing DI registration or configuration binding in the composition root.");
        }

        _logger.LogInformation("Startup registration smoke check passed: all critical bindings and services resolved.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void CheckOptions<TOptions>(List<string> failures, string label)
        where TOptions : class
    {
        try
        {
            var value = _serviceProvider.GetRequiredService<IOptionsMonitor<TOptions>>().CurrentValue;
            if (value is null)
                failures.Add($"{label} (options bound to null)");
        }
        catch (Exception ex)
        {
            failures.Add($"{label} ({ex.GetType().Name})");
        }
    }

    private void CheckRoot<TService>(List<string> failures, string label)
        where TService : class
    {
        try
        {
            _ = _serviceProvider.GetRequiredService<TService>();
        }
        catch (Exception ex)
        {
            failures.Add($"{label} ({ex.GetType().Name})");
        }
    }

    private static void CheckScoped<TService>(IServiceScope scope, List<string> failures, string label)
        where TService : class
    {
        try
        {
            _ = scope.ServiceProvider.GetRequiredService<TService>();
        }
        catch (Exception ex)
        {
            failures.Add($"{label} ({ex.GetType().Name})");
        }
    }
}
