using Application.AI.Common.Interfaces.Changes;
using Domain.Common.Config;
using Infrastructure.AI.Changes.Gates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Changes;

/// <summary>
/// One-shot startup validator for the ChangeProposal pipeline. Runs once via
/// <see cref="IHostedService.StartAsync"/> and refuses to boot the host when
/// the pipeline is enabled but the registered services or config make
/// production use unsafe.
/// </summary>
/// <remarks>
/// <para>
/// Checks performed (only when <c>AppConfig.AI.Changes.Enabled</c> is true):
/// </para>
/// <list type="number">
///   <item><description>
///     <b>In-memory store outside Development</b> — if the active
///     <see cref="IChangeProposalStore"/> is <see cref="InMemoryChangeProposalStore"/>
///     and <see cref="IHostEnvironment.EnvironmentName"/> is not Development,
///     throws unless <c>AllowInMemoryStoreOutsideDevelopment</c> is explicitly
///     true. Proposals lost across restarts strand <c>AwaitingApproval</c>
///     state silently; fail-fast at boot is louder than fail-quiet at restart.
///   </description></item>
///   <item><description>
///     <b>Empty approver list with default router</b> — if the active
///     <see cref="IChangeApprovalRouter"/> is
///     <see cref="EscalationServiceApprovalRouter"/> and
///     <c>DefaultApprovers</c> is empty, throws. The router would otherwise
///     fail every approval at gate-evaluation time per proposal; surfacing the
///     misconfig at boot is honest.
///   </description></item>
/// </list>
/// <para>
/// When the pipeline is disabled the validator no-ops — the rest of the graph
/// is inert anyway, and consumers exploring the template shouldn't be blocked
/// from running the host.
/// </para>
/// </remarks>
public sealed class ChangeProposalStartupValidator : IHostedService
{
    private readonly IChangeProposalStore _store;
    private readonly IChangeApprovalRouter _router;
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ChangeProposalStartupValidator> _logger;

    /// <summary>Initializes a new <see cref="ChangeProposalStartupValidator"/>.</summary>
    /// <remarks>
    /// <see cref="IHostEnvironment"/> is resolved lazily from
    /// <see cref="IServiceProvider"/> inside <see cref="StartAsync"/> rather
    /// than required at construction so DI tests that enumerate
    /// <c>GetServices&lt;IHostedService&gt;()</c> without a registered host
    /// environment don't fail at materialization. Real hosts always register
    /// <see cref="IHostEnvironment"/>; if absent at StartAsync the validator
    /// treats the environment as unknown and applies the production-strict
    /// rule for the in-memory store.
    /// </remarks>
    public ChangeProposalStartupValidator(
        IChangeProposalStore store,
        IChangeApprovalRouter router,
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        ILogger<ChangeProposalStartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _router = router;
        _services = services;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var changes = _config.CurrentValue.AI.Changes;
        if (!changes.Enabled)
        {
            return Task.CompletedTask;
        }

        var environment = _services.GetService<IHostEnvironment>();
        ValidateInMemoryStore(changes, environment);
        ValidateDefaultApprovers(changes);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void ValidateInMemoryStore(
        Domain.Common.Config.AI.ChangesConfig changes,
        IHostEnvironment? environment)
    {
        if (_store is not InMemoryChangeProposalStore)
        {
            // Consumer wired a durable implementation — nothing to validate.
            return;
        }

        var envName = environment?.EnvironmentName ?? "Unknown";
        var isDevelopment = environment?.IsDevelopment() ?? false;

        if (isDevelopment)
        {
            _logger.LogInformation(
                "ChangeProposal pipeline using InMemoryChangeProposalStore in {Environment} environment.",
                envName);
            return;
        }

        if (changes.AllowInMemoryStoreOutsideDevelopment)
        {
            _logger.LogWarning(
                "ChangeProposal pipeline using InMemoryChangeProposalStore in {Environment} environment. " +
                "Proposal state will not survive host restarts. Opt-in is explicit via " +
                "AppConfig.AI.Changes.AllowInMemoryStoreOutsideDevelopment.",
                envName);
            return;
        }

        throw new InvalidOperationException(
            $"ChangeProposal pipeline is Enabled in environment '{envName}' but the " +
            $"registered IChangeProposalStore is {nameof(InMemoryChangeProposalStore)}. " +
            "Proposals are lost on host restart, which silently strands AwaitingApproval state. " +
            "Either: (a) register a durable IChangeProposalStore implementation in DI before " +
            "AddInfrastructureAIDependencies, or (b) explicitly opt in by setting " +
            "AppConfig.AI.Changes.AllowInMemoryStoreOutsideDevelopment = true.");
    }

    private void ValidateDefaultApprovers(Domain.Common.Config.AI.ChangesConfig changes)
    {
        if (_router is not EscalationServiceApprovalRouter)
        {
            // Consumer replaced the router — it manages its own approver list.
            return;
        }

        if (changes.DefaultApprovers.Count > 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "ChangeProposal pipeline is Enabled and the default EscalationServiceApprovalRouter is " +
            "registered, but AppConfig.AI.Changes.DefaultApprovers is empty. Every approval would " +
            "throw at gate-evaluation time. " +
            "Either: (a) configure at least one approver in AppConfig.AI.Changes.DefaultApprovers, " +
            "or (b) register a custom IChangeApprovalRouter that supplies its own approver list.");
    }
}
