using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Egress;

/// <summary>
/// One-shot startup validator for the egress layer. Runs via
/// <see cref="IHostedService.StartAsync"/> and refuses to boot the host when
/// the layer is enabled but the configuration is impossible.
/// </summary>
/// <remarks>
/// <para>
/// Checks performed (only when <c>AppConfig.AI.Egress.Enabled</c> is true):
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Plain-text HTTP outside Development</b> — if
///     <c>AllowPlainTextHttp</c> is true and the host environment is not
///     Development and <c>AllowPlainTextHttpOutsideDevelopment</c> is not
///     explicitly true, the validator throws. Plain-text outbound is a
///     credential-leak vector in any environment with a real network
///     adversary; fail-fast at boot is louder than fail-quiet at runtime.
///   </description></item>
///   <item><description>
///     <b>Malformed allowlist entries</b> — the validator constructs a
///     <see cref="DefaultEgressPolicy"/> from the configured allowlist, which
///     runs entry validation. A malformed entry (both Host and HostPattern
///     set, neither set, a pattern with a non-leftmost wildcard, etc.) throws
///     at boot.
///   </description></item>
/// </list>
/// <para>
/// When the layer is disabled the validator no-ops — the named HttpClient is
/// inert anyway and consumers exploring the template should not be blocked
/// from running the host.
/// </para>
/// </remarks>
public sealed class EgressStartupValidator : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<EgressStartupValidator> _logger;

    /// <summary>Initializes a new <see cref="EgressStartupValidator"/>.</summary>
    public EgressStartupValidator(
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        ILogger<EgressStartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var egress = _config.CurrentValue.AI.Egress;
        if (!egress.Enabled)
        {
            return Task.CompletedTask;
        }

        var environment = _services.GetService<IHostEnvironment>();
        ValidatePlainTextHttp(egress, environment);
        ValidateAllowlistShape(egress);

        _logger.LogInformation(
            "Egress layer enabled. Default allowlist has {EntryCount} entries; AllowPlainTextHttp = {AllowPlainTextHttp}.",
            egress.DefaultAllowlist.Count,
            egress.AllowPlainTextHttp);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidatePlainTextHttp(
        Domain.Common.Config.AI.EgressConfig egress,
        IHostEnvironment? environment)
    {
        if (!egress.AllowPlainTextHttp)
        {
            return;
        }

        var isDevelopment = environment?.IsDevelopment() ?? false;
        if (isDevelopment)
        {
            return;
        }

        if (egress.AllowPlainTextHttpOutsideDevelopment)
        {
            return;
        }

        var envName = environment?.EnvironmentName ?? "Unknown";
        throw new InvalidOperationException(
            $"Egress layer is Enabled in environment '{envName}' with AllowPlainTextHttp = true. " +
            "Plain-text HTTP outside Development is a credential-leak vector. " +
            "Either: (a) set AppConfig.AI.Egress.AllowPlainTextHttp = false, " +
            "or (b) explicitly opt in by setting AppConfig.AI.Egress.AllowPlainTextHttpOutsideDevelopment = true.");
    }

    private void ValidateAllowlistShape(Domain.Common.Config.AI.EgressConfig egress)
    {
        // Materialise the allowlist as domain entries. The DefaultEgressPolicy
        // constructor validates each entry; we re-use that validation here so
        // the boot-time error message is the same as the runtime one.
        var entries = EgressAllowlistMapper.Map(egress.DefaultAllowlist);

        try
        {
            _ = new DefaultEgressPolicy(
                entries,
                _services.GetRequiredService<ILogger<DefaultEgressPolicy>>(),
                _services.GetService<TimeProvider>() ?? TimeProvider.System);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                "Egress layer is Enabled but AppConfig.AI.Egress.DefaultAllowlist contains a malformed entry. " +
                "See inner exception for details.",
                ex);
        }
    }
}
