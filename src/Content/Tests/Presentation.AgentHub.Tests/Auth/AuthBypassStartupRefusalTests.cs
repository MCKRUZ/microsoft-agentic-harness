using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using Xunit;

namespace Presentation.AgentHub.Tests.Auth;

/// <summary>
/// A production AgentHub with sign-in bypassed refuses to start (#904). With the bypass every caller is one synthetic
/// identity, so one person's memory, conversations and erasure requests would be everyone's; outside Development that has
/// to be a deliberate, logged choice (<c>Auth:AllowOutsideDevelopment</c>), never a leftover setting.
/// </summary>
/// <remarks>
/// Boots the real <c>Program</c> with no authentication override. The settings go in as environment variables because
/// AgentHub reads them while registering services, before a factory's own configuration applies; this project runs its
/// tests one at a time, so setting them here cannot leak into another test's host.
/// </remarks>
public sealed class AuthBypassStartupRefusalTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Start_SignInBypassedOutsideDevelopmentWithoutTheOverride_RefusesWithTheReason(string environment)
    {
        var act = () => Boot(environment, allowOutsideDevelopment: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Auth:Disabled=true*'{environment}'*Refusing to start*Auth:AllowOutsideDevelopment=true*");
    }

    [Fact]
    public void Start_SignInBypassedOutsideDevelopmentWithTheOverride_Starts()
    {
        // The control: the same host with the deliberate override starts (the self-hosted deployment of #591).
        var act = () => Boot("Production", allowOutsideDevelopment: true);

        act.Should().NotThrow();
    }

    private static void Boot(string environment, bool allowOutsideDevelopment)
    {
        var variables = new Dictionary<string, string?>
        {
            ["Auth__Disabled"] = "true",
            ["Auth__AllowOutsideDevelopment"] = allowOutsideDevelopment ? "true" : null,
            ["DisableAzureConfigSources"] = "true",
            ["AppConfig__AI__AgentFramework__ClientType"] = "Echo",
            ["AppConfig__Observability__Exporters__Otlp__Enabled"] = "false",
        };
        var original = variables.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in variables)
                Environment.SetEnvironmentVariable(key, value);

            Directory.SetCurrentDirectory(Path.GetDirectoryName(typeof(Program).Assembly.Location)!);
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                // Same OTel/MeterProvider test-host workaround as every other AgentHub factory (see
                // TestWebApplicationFactory's remarks).
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<MeterProvider>();
                    services.AddOpenTelemetry().WithMetrics(m => m.AddPrometheusExporter());
                });
            });
            _ = factory.Server; // Starts the host, and with it the startup guard.
        }
        finally
        {
            foreach (var (key, value) in original)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
