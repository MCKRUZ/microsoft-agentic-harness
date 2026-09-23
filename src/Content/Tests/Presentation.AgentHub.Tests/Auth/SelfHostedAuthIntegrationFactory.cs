using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;

namespace Presentation.AgentHub.Tests.Auth;

/// <summary>
/// Boots the real host with the exact environment a self-hosted, non-Azure deployment ships
/// (<c>Auth:Disabled</c> + <c>Auth:AllowOutsideDevelopment</c>, non-Development environment) and
/// the REAL <see cref="Presentation.AgentHub.Auth.SelfHostedAuthHandler"/> auth pipeline —
/// deliberately does NOT override authentication with <c>TestAuthHandler</c> the way every other
/// AgentHub test factory does, because the property under test IS the real handler's privilege
/// level (issue #591's security-review finding: the bypass identity must hold no elevated roles).
/// </summary>
public sealed class SelfHostedAuthIntegrationFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _originalEnvVars = [];

    public string TempConversationsPath { get; } =
        Path.Combine(Path.GetTempPath(), $"agenthubtests-selfhostedauth-{Guid.NewGuid():N}");

    public SelfHostedAuthIntegrationFactory()
    {
        SetEnvVar("Auth__Disabled", "true");
        SetEnvVar("Auth__AllowOutsideDevelopment", "true");
        SetEnvVar("DisableAzureConfigSources", "true");
        SetEnvVar("AppConfig__AI__AgentFramework__ClientType", "Echo");
        SetEnvVar("AppConfig__Observability__Exporters__Otlp__Enabled", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.SetCurrentDirectory(
            Path.GetDirectoryName(typeof(Program).Assembly.Location)!);

        // Non-Development: AuthBypassPolicy.GetBypassSchemeName must resolve to SelfHostedAuthHandler
        // here, not DevAuthHandler — that's the exact distinction this factory exists to exercise.
        builder.UseEnvironment("Container");

        builder.ConfigureServices(services =>
        {
            // Same OTel/MeterProvider test-host workaround as every other AgentHub factory — see
            // TestWebApplicationFactory's remarks.
            services.RemoveAll<MeterProvider>();
            services.AddOpenTelemetry().WithMetrics(m => m.AddPrometheusExporter());
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSignalR(o => o.EnableDetailedErrors = true);
            TestConversationStore.UseIsolatedDirectory(services, TempConversationsPath);
            // No auth override here — the whole point is exercising the real SelfHostedAuthHandler.
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (Directory.Exists(TempConversationsPath))
                Directory.Delete(TempConversationsPath, recursive: true);

            foreach (var (name, value) in _originalEnvVars)
                Environment.SetEnvironmentVariable(name, value);
        }

        base.Dispose(disposing);
    }

    private void SetEnvVar(string name, string? value)
    {
        _originalEnvVars[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }
}
