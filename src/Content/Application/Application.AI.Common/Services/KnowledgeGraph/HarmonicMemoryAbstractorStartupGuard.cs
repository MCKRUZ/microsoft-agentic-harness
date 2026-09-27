using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Domain.Common.Config.AI.HarmonicMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.KnowledgeGraph;

/// <summary>
/// Fails host startup when <c>AppConfig:AI:HarmonicMemory:Mode</c> is raised above
/// <see cref="HarmonicMemoryMode.Off"/> but the resolved <see cref="IMemoryAbstractor"/> — or, in
/// <see cref="HarmonicMemoryMode.Full"/>, the resolved <see cref="IMemoryConsolidator"/> — is still its
/// fail-fast <c>NotConfigured*</c> placeholder.
/// </summary>
/// <remarks>
/// <para>
/// Without this guard, the exact same misconfiguration is only discovered the first time the write
/// path actually reaches the unconfigured seam inside a live conversation turn — the abstractor on
/// every <c>RememberAsync</c> call, the consolidator only once a similar existing entry triggers
/// consolidation — and when that call runs through the unattended fact-extraction pipeline, the thrown
/// exception is caught and merely logged as a warning, so every future write silently fails with no
/// operator-visible signal at all (see issue #598/#600 for the sibling defects this masked). Registered
/// unconditionally by <see cref="Extensions.HarmonicMemoryDependencyInjection.AddHarmonicMemoryDependencies"/>;
/// the check itself is a no-op whenever Mode is <see cref="HarmonicMemoryMode.Off"/> (the default) or a
/// consumer has registered real agent-backed implementations. The consolidator is checked only in
/// <see cref="HarmonicMemoryMode.Full"/>, matching <c>NotConfiguredMemoryConsolidator</c>'s own doc
/// comment that it is unreached in <see cref="HarmonicMemoryMode.AbstractOnly"/>.
/// </para>
/// <para>
/// The check runs once, at startup. If a hot config reload later raises <c>Mode</c> with no real
/// implementation still registered, this guard does not re-fire — the original first-write failure
/// this guard exists to prevent returns for that case, since
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> is only read once, in
/// <see cref="StartAsync"/>.
/// </para>
/// <para>
/// Resolves both seams from a freshly created <see cref="IServiceScope"/> rather than
/// constructor-injecting them, because this guard is itself a singleton — a consumer's agent-backed
/// implementation is free to register at any lifetime, including Scoped (plausible for one that needs
/// per-conversation state), and a singleton that ctor-injects a scoped dependency fails
/// <c>ValidateOnBuild</c>/<c>ValidateScopes</c> host validation outright, on every boot, regardless of
/// <c>Mode</c> — replacing this guard's clear, actionable error with an opaque DI captive-dependency
/// crash. See the same pattern in <c>WorkMemorySynthesisBackgroundService</c>.
/// </para>
/// </remarks>
public sealed class HarmonicMemoryAbstractorStartupGuard : IHostedService
{
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Initializes a new instance of the <see cref="HarmonicMemoryAbstractorStartupGuard"/> class.</summary>
    /// <param name="options">Live app configuration, read for <c>AI.HarmonicMemory.Mode</c>.</param>
    /// <param name="scopeFactory">Used to resolve <see cref="IMemoryAbstractor"/>/<see cref="IMemoryConsolidator"/> at whatever lifetime a consumer registered them.</param>
    public HarmonicMemoryAbstractorStartupGuard(IOptionsMonitor<AppConfig> options, IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _options = options;
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = _options.CurrentValue.AI.HarmonicMemory.Mode;
        if (mode == HarmonicMemoryMode.Off)
            return Task.CompletedTask;

        using var scope = _scopeFactory.CreateScope();

        var abstractor = scope.ServiceProvider.GetRequiredService<IMemoryAbstractor>();
        if (abstractor is NotConfiguredMemoryAbstractor)
        {
            throw new InvalidOperationException(
                "AppConfig:AI:HarmonicMemory:Mode is " + mode + " but no IMemoryAbstractor is " +
                "configured — the default NotConfiguredMemoryAbstractor would throw on the first " +
                "RememberAsync call instead. Register an agent-backed IMemoryAbstractor " +
                "implementation (e.g. in Infrastructure.AI), or set Mode back to Off.");
        }

        if (mode == HarmonicMemoryMode.Full)
        {
            var consolidator = scope.ServiceProvider.GetRequiredService<IMemoryConsolidator>();
            if (consolidator is NotConfiguredMemoryConsolidator)
            {
                throw new InvalidOperationException(
                    "AppConfig:AI:HarmonicMemory:Mode is Full but no IMemoryConsolidator is " +
                    "configured — the default NotConfiguredMemoryConsolidator would throw the first " +
                    "time a similar existing entry triggers consolidation, instead of failing at " +
                    "startup. Register an agent-backed IMemoryConsolidator implementation (e.g. in " +
                    "Infrastructure.AI), or set Mode to AbstractOnly, which never consolidates.");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
