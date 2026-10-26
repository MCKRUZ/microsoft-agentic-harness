using Application.AI.Common.Interfaces.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Resolves the <see cref="IAuditPseudonymizer"/> when the host starts (#903), so the audit pseudonym key is read, or its
/// key file made, before any audit record is written.
/// </summary>
/// <remarks>
/// Without this the key would be resolved by the first audit write, and an unusable key or a key file the process cannot
/// write would surface as a failed escalation or change record in the middle of a request, rather than as a host that
/// refuses to start with the reason.
/// </remarks>
public sealed class AuditPseudonymKeyStartup : IHostedService
{
    private readonly IServiceProvider _services;

    /// <summary>Initializes a new instance of the <see cref="AuditPseudonymKeyStartup"/> class.</summary>
    /// <param name="services">The root provider the singleton pseudonymizer is resolved from.</param>
    public AuditPseudonymKeyStartup(IServiceProvider services) => _services = services;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = _services.GetRequiredService<IAuditPseudonymizer>();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
