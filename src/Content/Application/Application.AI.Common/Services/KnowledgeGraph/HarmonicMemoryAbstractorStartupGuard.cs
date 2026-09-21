using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Domain.Common.Config.AI.HarmonicMemory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.KnowledgeGraph;

/// <summary>
/// Fails host startup when <c>AppConfig:AI:HarmonicMemory:Mode</c> is raised above
/// <see cref="HarmonicMemoryMode.Off"/> but the resolved <see cref="IMemoryAbstractor"/> is still
/// the fail-fast <see cref="NotConfiguredMemoryAbstractor"/> placeholder.
/// </summary>
/// <remarks>
/// <para>
/// Without this guard, the exact same misconfiguration is only discovered on the first
/// <c>RememberAsync</c> call inside a live conversation turn — and when that call runs through
/// the unattended fact-extraction pipeline, the thrown exception is caught and merely logged as a
/// warning, so every future write silently fails with no operator-visible signal at all (see
/// issue #598/#600 for the sibling defects this masked). Registered unconditionally by
/// <see cref="Extensions.HarmonicMemoryDependencyInjection.AddHarmonicMemoryDependencies"/>; the
/// check itself is a no-op whenever Mode is <see cref="HarmonicMemoryMode.Off"/> (the default) or
/// a consumer has registered a real agent-backed implementation.
/// </para>
/// <para>
/// The check runs once, at startup. If a hot config reload later raises <c>Mode</c> above
/// <see cref="HarmonicMemoryMode.Off"/> with no real abstractor still registered, this guard does
/// not re-fire — the original first-write failure this guard exists to prevent returns for that
/// case, since <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> is only read
/// once, in <see cref="StartAsync"/>.
/// </para>
/// </remarks>
public sealed class HarmonicMemoryAbstractorStartupGuard : IHostedService
{
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly IMemoryAbstractor _abstractor;

    /// <summary>Initializes a new instance of the <see cref="HarmonicMemoryAbstractorStartupGuard"/> class.</summary>
    /// <param name="options">Live app configuration, read for <c>AI.HarmonicMemory.Mode</c>.</param>
    /// <param name="abstractor">The <see cref="IMemoryAbstractor"/> resolved by the container.</param>
    public HarmonicMemoryAbstractorStartupGuard(IOptionsMonitor<AppConfig> options, IMemoryAbstractor abstractor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(abstractor);
        _options = options;
        _abstractor = abstractor;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = _options.CurrentValue.AI.HarmonicMemory.Mode;
        if (mode != HarmonicMemoryMode.Off && _abstractor is NotConfiguredMemoryAbstractor)
        {
            throw new InvalidOperationException(
                "AppConfig:AI:HarmonicMemory:Mode is " + mode + " but no IMemoryAbstractor is " +
                "configured — the default NotConfiguredMemoryAbstractor would throw on the first " +
                "RememberAsync call instead. Register an agent-backed IMemoryAbstractor " +
                "implementation (e.g. in Infrastructure.AI), or set Mode back to Off.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
