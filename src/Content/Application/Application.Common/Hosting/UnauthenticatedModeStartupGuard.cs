using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Application.Common.Hosting;

/// <summary>
/// Stands guard at startup over a host that serves without authentication (#904): a sign-in bypass in AgentHub, the
/// anonymous mode of the bundle API or of the MCP server.
/// </summary>
/// <remarks>
/// <para>
/// With authentication off every caller is one synthetic identity. Per-user isolation is gone: one memory, one
/// conversation owner, one audit subject, and a person's request to erase their data would reach everyone's. That is a
/// reasonable trade on a developer's machine and a serious one anywhere else (Art. 25 and 32 GDPR).
/// </para>
/// <para>
/// So outside the Development environment the host refuses to start unless the deployment has set a second, explicit
/// setting that says it means it, such as a self-hosted install behind a private network boundary (#591). When it has,
/// the choice is logged at Critical on every start, so it can be found in any log review. In Development the mode is
/// allowed and logged as a warning.
/// </para>
/// <para>
/// A lifecycle hosted service rather than a check during registration, so the decision uses the environment the host
/// actually runs in. The refusal is thrown from <see cref="StartingAsync"/>, which the host runs for every lifecycle
/// service before it starts <em>any</em> hosted service: otherwise the background services registered ahead of this one
/// (run resumption, schedule dispatch, retention sweeps) would already be doing work by the time it refused. Whether the
/// override is set is read when the guard is registered; only the environment is read at startup.
/// </para>
/// </remarks>
public sealed class UnauthenticatedModeStartupGuard : IHostedLifecycleService
{
    private readonly string _mode;
    private readonly string _overrideSetting;
    private readonly bool _overridden;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<UnauthenticatedModeStartupGuard> _logger;

    /// <summary>Initializes a new instance of the <see cref="UnauthenticatedModeStartupGuard"/> class.</summary>
    /// <param name="mode">What is unauthenticated and which setting turned it on, for the messages.</param>
    /// <param name="overrideSetting">The setting that allows the mode outside Development.</param>
    /// <param name="overridden">Whether <paramref name="overrideSetting"/> is set.</param>
    /// <param name="environment">The environment the host runs in.</param>
    /// <param name="logger">Logger for the warning or critical entry.</param>
    public UnauthenticatedModeStartupGuard(
        string mode,
        string overrideSetting,
        bool overridden,
        IHostEnvironment environment,
        ILogger<UnauthenticatedModeStartupGuard> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(overrideSetting);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _mode = mode;
        _overrideSetting = overrideSetting;
        _overridden = overridden;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The mode is on outside Development without the override.</exception>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_environment.IsDevelopment())
        {
            _logger.LogWarning(
                "{Mode} is on: every caller is one synthetic identity, so per-user isolation does not apply. Allowed " +
                "because the environment is Development.",
                _mode);
            return Task.CompletedTask;
        }

        if (!_overridden)
        {
            throw new InvalidOperationException(
                $"{_mode} is on in the '{_environment.EnvironmentName}' environment. Refusing to start: without " +
                "authentication every caller is one synthetic identity, so one person's data, memory and erasure " +
                "requests would be everyone's. Configure authentication, or, if this deployment deliberately runs " +
                $"without it (for example behind a private network boundary), set {_overrideSetting}=true.");
        }

        _logger.LogCritical(
            "{Mode} is on in the {Environment} environment because {OverrideSetting}=true. Every caller is one " +
            "synthetic identity: per-user isolation, self-service erasure and per-person audit attribution do not " +
            "apply. Keep this host off any network a person outside the deploying organisation can reach.",
            _mode, _environment.EnvironmentName, _overrideSetting);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
