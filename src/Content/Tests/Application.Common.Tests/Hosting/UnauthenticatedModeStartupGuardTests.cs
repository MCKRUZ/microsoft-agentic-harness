using Application.Common.Hosting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Common.Tests.Hosting;

/// <summary>
/// <see cref="UnauthenticatedModeStartupGuard"/> (#904): outside Development a host may serve without authentication
/// only when the deployment says so explicitly, and that choice is always logged.
/// </summary>
public sealed class UnauthenticatedModeStartupGuardTests
{
    private readonly Mock<ILogger<UnauthenticatedModeStartupGuard>> _logger = new();

    private UnauthenticatedModeStartupGuard Guard(string environment, bool overridden) => new(
        "Test anonymous mode (Test:AllowAnonymous=true)",
        "Test:AllowAnonymousOutsideDevelopment",
        overridden,
        Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment),
        _logger.Object);

    private int Logged(LogLevel level) => _logger.Invocations.Count(i => (LogLevel)i.Arguments[0] == level);

    [Fact]
    public async Task StartingAsync_Development_StartsAndWarns()
    {
        await Guard("Development", overridden: false).StartingAsync(CancellationToken.None);

        Logged(LogLevel.Warning).Should().Be(1);
        Logged(LogLevel.Critical).Should().Be(0);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Container")]
    public async Task StartingAsync_OutsideDevelopmentWithoutTheOverride_RefusesNamingTheOverride(string environment)
    {
        var act = () => Guard(environment, overridden: false).StartingAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*'{environment}'*Refusing to start*Test:AllowAnonymousOutsideDevelopment=true*");
    }

    [Fact]
    public async Task HostStart_Refused_NoHostedServiceRegisteredBeforeTheGuardEverStarts()
    {
        // The host registers background services (run resumption, schedule dispatch, retention sweeps) ahead of the
        // guard. A refusal from StartAsync would come after they had begun work; from StartingAsync it comes first.
        var background = new RecordingHostedService();
        using var host = new HostBuilder()
            .UseEnvironment("Production")
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostedService>(background);
                services.AddHostedService(sp => new UnauthenticatedModeStartupGuard(
                    "Test anonymous mode", "Test:Override", overridden: false,
                    sp.GetRequiredService<IHostEnvironment>(),
                    _logger.Object));
            })
            .Build();

        var act = () => host.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        background.Started.Should().BeFalse("no hosted service may start once the guard has refused");
    }

    private sealed class RecordingHostedService : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task StartingAsync_OutsideDevelopmentWithTheOverride_StartsAndLogsCritical()
    {
        // The deliberate choice must be findable in any log review, on every start.
        await Guard("Production", overridden: true).StartingAsync(CancellationToken.None);

        Logged(LogLevel.Critical).Should().Be(1);
    }
}
