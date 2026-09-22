using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;

namespace Presentation.AgentHub.Tests.Composition;

/// <summary>
/// Boots the real, unmodified production host (real <c>Program.cs</c>, real
/// <c>GetServices()</c> composition root, real <c>ChatClientFactory</c>) the way a self-hosted,
/// non-Azure deployment does — see issue #591. Deliberately does NOT replace
/// <see cref="Application.AI.Common.Interfaces.IChatClientFactory"/> the way
/// <see cref="IntegrationTestFactory"/> and <see cref="TestWebApplicationFactory"/> do: the whole
/// point is proving the real factory resolves <c>ClientType=Echo</c> end to end, with zero Azure
/// configuration present, through the actual composition root — not a stand-in for it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Presentation.Common.Extensions.IServiceCollectionExtensions.GetServices"/> calls
/// <c>AppConfigHelper.LoadAppConfig()</c> itself and ignores <c>WebApplicationFactory</c>'s own
/// <c>ConfigureAppConfiguration</c> hook entirely — the only channel that reaches the real
/// <c>AppConfig</c> binding is the environment-variable source <c>LoadAppConfig</c> adds itself.
/// This factory therefore mutates real process environment variables in its constructor and
/// restores them on <see cref="Dispose"/>. That is safe only because this assembly disables test
/// parallelization (see <c>AssemblyInfo.cs</c>, issue #261) — every test in this assembly already
/// runs serially, so no other test observes the mutated environment mid-run.
/// </para>
/// </remarks>
public sealed class NonAzureHeadlessFactory : WebApplicationFactory<Program>
{
    private static readonly string[] EnvVarsToClear =
    [
        "AzureKeyVaultUri",
        "AzureAppConfigConnectionString",
        "APPLICATIONINSIGHTS_CONNECTION_STRING",
        "AZURE_CLIENT_ID",
        "AZURE_TENANT_ID",
        "AZURE_CLIENT_SECRET",
        "AZURE_FEDERATED_TOKEN_FILE",
    ];

    private readonly Dictionary<string, string?> _originalEnvVars = [];

    /// <summary>Isolated temp directory for conversation storage.</summary>
    public string TempConversationsPath { get; } =
        Path.Combine(Path.GetTempPath(), $"agenthubtests-nonazure-{Guid.NewGuid():N}");

    /// <summary>Sets the process environment to the shape a non-Azure container ships with.</summary>
    public NonAzureHeadlessFactory()
    {
        SetEnvVar("DisableAzureConfigSources", "true");
        SetEnvVar("AppConfig__AI__AgentFramework__ClientType", "Echo");
        SetEnvVar("AppConfig__Observability__Exporters__Otlp__Enabled", "false");

        foreach (var name in EnvVarsToClear)
            SetEnvVar(name, null);
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.SetCurrentDirectory(
            Path.GetDirectoryName(typeof(Program).Assembly.Location)!);

        // A real self-hosted deployment sets Auth:AllowOutsideDevelopment rather than lying about
        // the environment (see AuthBypassPolicy) — but auth itself is not what this suite verifies,
        // so a test auth handler stands in exactly as it does for every other AgentHub test suite.
        builder.UseEnvironment("Production");

        builder.ConfigureServices(services =>
        {
            // The production OpenTelemetry pipeline checks Assembly.GetEntryAssembly() against
            // WebTelemetryProjects. In the test runner the entry assembly is "testhost", so
            // AddDesktopTelemetry() runs instead of AddWebTelemetry() — registering a standalone
            // MeterProvider without the Prometheus exporter, which MapPrometheusScrapingEndpoint()
            // in Program.cs requires. Same fix as TestWebApplicationFactory (see its remarks).
            services.RemoveAll<MeterProvider>();
            services.AddOpenTelemetry().WithMetrics(m => m.AddPrometheusExporter());
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSignalR(o => o.EnableDetailedErrors = true);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

            TestConversationStore.UseIsolatedDirectory(services, TempConversationsPath);
        });
    }

    /// <inheritdoc/>
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
