namespace Presentation.AgentHub.Auth;

/// <summary>
/// The single source of truth for whether AgentHub's Entra sign-in requirement is bypassed in
/// favor of <see cref="DevAuthHandler"/>. Extracted so <see cref="DependencyInjection"/> (which
/// registers the auth scheme) and <see cref="Controllers.ConfigController"/> (which reports the
/// active mode to callers) can never disagree — before this existed, the same
/// <c>IsDevelopment() &amp;&amp; Auth:Disabled</c> expression was hand-copied in both places.
/// </summary>
public static class AuthBypassPolicy
{
    /// <summary>
    /// True when Entra sign-in should be bypassed. Requires <c>Auth:Disabled=true</c> AND either
    /// the host is running in the Development environment, or the deployment has explicitly opted
    /// in via <c>Auth:AllowOutsideDevelopment=true</c>.
    /// </summary>
    /// <remarks>
    /// The second condition exists for self-hosted, non-Azure deployments (see issue #591) that
    /// have no Entra tenant to authenticate against at all. Without it, the only way to bypass
    /// sign-in outside Development was to also mislabel the environment as "Development" — which
    /// silently turns off unrelated production hardening (forced HTTPS, stricter error responses).
    /// A deployment that sets this flag is making a deliberate, reviewable decision to run without
    /// authentication (e.g. behind a private network boundary), not accidentally inheriting one.
    /// </remarks>
    public static bool IsBypassed(IHostEnvironment environment, IConfiguration configuration) =>
        GetBypassSchemeName(environment, configuration) is not null;

    /// <summary>
    /// The authentication scheme to register when sign-in is bypassed, or <c>null</c> when Entra
    /// sign-in is required. Development bypass and the self-hosted (<c>AllowOutsideDevelopment</c>)
    /// bypass deliberately use two DIFFERENT handlers with two different privilege levels —
    /// <see cref="DevAuthHandler"/>'s synthetic principal holds escalation-admin,
    /// change-proposal-admin, and drift/registry-operate roles appropriate for a developer
    /// exercising every code path locally; <see cref="SelfHostedAuthHandler"/>'s principal holds
    /// none of those, because a real, network-reachable deployment with sign-in off must not grant
    /// operator-level power to every unauthenticated caller by accident. When both conditions are
    /// true (Development AND AllowOutsideDevelopment), Development wins — unchanged local dev
    /// behavior takes precedence over a flag that exists for a different deployment shape.
    /// </summary>
    public static string? GetBypassSchemeName(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Auth:Disabled"))
            return null;

        if (environment.IsDevelopment())
            return DevAuthHandler.SchemeName;

        if (configuration.GetValue<bool>("Auth:AllowOutsideDevelopment"))
            return SelfHostedAuthHandler.SchemeName;

        return null;
    }
}
