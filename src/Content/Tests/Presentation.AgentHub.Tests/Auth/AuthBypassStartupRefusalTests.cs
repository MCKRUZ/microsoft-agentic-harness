using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Presentation.AgentHub.Auth;
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
    public void Start_SignInBypassedOutsideDevelopmentWithTheOverride_StartsSelfHostedAndLogsCritical()
    {
        // The control: the same host with the deliberate override starts (the self-hosted deployment of #591), under the
        // low-privilege self-hosted handler, never the developer one, and with the choice on the record.
        var logs = new CapturingLoggerProvider();

        var scheme = Boot("Production", allowOutsideDevelopment: true, logs);

        scheme.Should().Be(SelfHostedAuthHandler.SchemeName);
        logs.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Critical && e.Message.Contains("Auth:AllowOutsideDevelopment=true"));
    }

    private static string? Boot(string environment, bool allowOutsideDevelopment, ILoggerProvider? logs = null)
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
        var originalDirectory = Directory.GetCurrentDirectory();
        try
        {
            foreach (var (key, value) in variables)
                Environment.SetEnvironmentVariable(key, value);

            Directory.SetCurrentDirectory(Path.GetDirectoryName(typeof(Program).Assembly.Location)!);
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                if (logs is not null)
                    builder.ConfigureLogging(logging => logging.AddProvider(logs));
                // Same OTel/MeterProvider test-host workaround as every other AgentHub factory (see
                // TestWebApplicationFactory's remarks).
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<MeterProvider>();
                    services.AddOpenTelemetry().WithMetrics(m => m.AddPrometheusExporter());
                });
            });
            _ = factory.Server; // Starts the host, and with it the startup guard.
            return factory.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>()
                .GetDefaultAuthenticateSchemeAsync().GetAwaiter().GetResult()?.Name;
        }
        finally
        {
            foreach (var (key, value) in original)
                Environment.SetEnvironmentVariable(key, value);
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => provider.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
