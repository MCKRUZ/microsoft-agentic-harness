using System.Threading;
using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Infrastructure.AI.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.BackgroundServices;

/// <summary>
/// Watches every configured agent search path for <c>AGENT.md</c> changes and invalidates
/// <see cref="IAgentRegistryRefresher"/> so the next read re-scans the filesystem — the automatic
/// half of issue #705's "no restart to add/remove/refresh an agent" fix. The other half is the
/// operator-triggered refresh command; this service's job is that an ordinary file save is enough
/// on its own, with no one having to call anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>No filesystem work in the constructor.</b> The production composition root's
/// <c>ValidateOnBuild</c> eagerly constructs every registered <see cref="IHostedService"/> — a
/// watcher created there, rather than in <see cref="ExecuteAsync"/>, would touch the filesystem
/// during service-graph validation instead of host startup.
/// </para>
/// <para>
/// <b>Debounced, not immediate.</b> A single manifest edit produces several filesystem events in
/// quick succession (most editors write via a temp file plus rename). Every event resets one shared
/// timer rather than triggering its own invalidation, so a burst collapses into exactly one
/// invalidation fired <see cref="AgentsConfig.ChangeDebounceMilliseconds"/> after the <em>last</em>
/// event in the burst — never after the first, which could still be reading a half-written file.
/// </para>
/// <para>
/// <b>Invalidate, not eager refresh.</b> The watcher calls <see cref="IAgentRegistryRefresher.Invalidate"/>,
/// not <see cref="IAgentRegistryRefresher.Refresh"/>: dropping the cache is cheap and idempotent, so a
/// storm of filesystem events during, say, a plugin sync collapses to nothing more than "the next
/// read re-scans" — there is no summary an unattended background trigger could usefully report
/// anyway. <c>Refresh()</c> is reserved for the operator-triggered command, which has a caller who
/// wants to know what changed.
/// </para>
/// <para>
/// <b><see cref="FileSystemWatcher.Error"/> is handled.</b> A watcher whose internal event buffer
/// overflows silently stops delivering events rather than throwing — without handling this, a burst
/// of filesystem activity elsewhere in a watched tree could quietly disable live-reload for the rest
/// of the process's life. On <c>Error</c>, this service invalidates unconditionally (the watcher may
/// have missed changes) and rebuilds every watcher from the current configuration.
/// </para>
/// </remarks>
public sealed class AgentManifestWatcherService : BackgroundService
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IAgentRegistryRefresher _refresher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentManifestWatcherService> _logger;

    private readonly Lock _watchersLock = new();
    private List<FileSystemWatcher> _watchers = [];
    private IReadOnlyList<string> _watchedPaths = [];
    private ITimer? _debounceTimer;
    private IDisposable? _optionsChangeSubscription;

    /// <summary>
    /// Set once by <see cref="Shutdown"/>. A filesystem callback that arrives after shutdown has
    /// already disposed the watchers and timer could otherwise recreate a debounce timer
    /// (<see cref="ScheduleInvalidate"/>) or new watchers (<see cref="RetargetWatchers"/>) that
    /// nothing ever cleans up (correctness-gate advisory on #705) — a leak, since the window between
    /// a callback firing and shutdown completing is narrow, not a correctness failure, but cheap to
    /// close outright.
    /// </summary>
    private bool _stopped;

    /// <summary>Initialises the watcher with its dependencies. Touches no filesystem state.</summary>
    /// <param name="appConfig">Monitor over the live application configuration (agent search paths and watch settings).</param>
    /// <param name="refresher">The registry seam this service invalidates on a detected change.</param>
    /// <param name="timeProvider">Drives the debounce timer — injected so the schedule is testable.</param>
    /// <param name="logger">Logger for watcher lifecycle and change diagnostics.</param>
    public AgentManifestWatcherService(
        IOptionsMonitor<AppConfig> appConfig,
        IAgentRegistryRefresher refresher,
        TimeProvider timeProvider,
        ILogger<AgentManifestWatcherService> logger)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(refresher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _appConfig = appConfig;
        _refresher = refresher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The paths currently being watched. Empty when disabled or no paths resolved.</summary>
    internal IReadOnlyList<string> WatchedPaths
    {
        get
        {
            lock (_watchersLock)
                return _watchedPaths;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The OnChange subscription is registered UNCONDITIONALLY, before looking at the current
        // value, and the service stays alive (the Task.Delay below) regardless of the initial
        // WatchForChanges setting. The first cut of this method returned immediately when disabled
        // at startup without ever subscribing — so a host that starts with watching off and later
        // flips AI:Agents:WatchForChanges to true via a hot-reloadable config source (file-based
        // appsettings reload, Azure App Configuration) had no live subscription to react to that
        // flip: watching stayed off until a restart, asymmetric with the reverse (true→false), which
        // DID work because the subscription was already active in that case (code review on #705).
        _optionsChangeSubscription = _appConfig.OnChange((config, _) => RetargetWatchers(config.AI?.Agents));
        RetargetWatchers(_appConfig.CurrentValue.AI?.Agents);

        try
        {
            // All real work happens via FileSystemWatcher event callbacks; this just holds the
            // service alive until the host asks it to stop.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — expected.
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>
    /// Rebuilds the watcher set from <paramref name="agentsConfig"/>'s currently-resolved paths,
    /// unless they are identical to what is already being watched — a no-op guard that absorbs both
    /// a config reload that did not actually change agent paths and the double-fire some file-based
    /// configuration providers produce for a single edit.
    /// </summary>
    /// <param name="agentsConfig">The current agent configuration, or <see langword="null"/>.</param>
    /// <param name="forceRebuild">
    /// Bypasses the unchanged-paths no-op guard even though the resolved path SET is identical — used
    /// by <see cref="OnWatcherError"/>, where the watcher INSTANCES (not the paths) are what need
    /// replacing. A previous version achieved this by clobbering <c>_watchedPaths</c> to force a
    /// mismatch; an explicit parameter (code review on #705) keeps that intent from silently breaking
    /// if this method ever grows a second no-op condition that runs before the path check.
    /// </param>
    private void RetargetWatchers(AgentsConfig? agentsConfig, bool forceRebuild = false)
    {
        if (agentsConfig?.WatchForChanges != true)
        {
            if (_watchers.Count > 0)
            {
                _logger.LogInformation(
                    "Agent manifest watching disabled (AI:Agents:WatchForChanges) — changes require an " +
                    "explicit refresh or a process restart to be picked up");
            }

            StopWatching();
            return;
        }

        var resolvedPaths = AgentSearchPathResolver.Resolve(agentsConfig, _logger);

        lock (_watchersLock)
        {
            if (_stopped)
                return;

            if (!forceRebuild && _watchedPaths.SequenceEqual(resolvedPaths, StringComparer.OrdinalIgnoreCase))
                return;

            DisposeWatchersNoLock();

            if (resolvedPaths.Count == 0)
            {
                _watchedPaths = [];
                return;
            }

            var watchers = new List<FileSystemWatcher>(resolvedPaths.Count);
            foreach (var path in resolvedPaths)
            {
                FileSystemWatcher watcher;
                try
                {
                    watcher = new FileSystemWatcher(path)
                    {
                        IncludeSubdirectories = true,
                        // No Filter (correctness gate finding on #705): a Filter of "AGENT.md" only
                        // matches an item literally named that, so moving or renaming an entire agent
                        // folder — `mv billing-agent agents/billing-agent`, or an Explorer delete,
                        // which moves the folder to the Recycle Bin — raises a single folder-level
                        // event whose Name is the folder, not "AGENT.md", and that event was silently
                        // dropped: the new agent stayed invisible, and a "deleted" agent kept being
                        // served indefinitely. Watching every item and adding NotifyFilters.DirectoryName
                        // catches both; the handler doesn't need to know WHAT changed since it always
                        // just marks the registry stale for a full rescan.
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    };

                    watcher.Created += OnManifestChanged;
                    watcher.Changed += OnManifestChanged;
                    watcher.Deleted += OnManifestChanged;
                    watcher.Renamed += OnManifestChanged;
                    watcher.Error += OnWatcherError;

                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex)
                {
                    // A path that exists (AgentSearchPathResolver already checked Directory.Exists)
                    // can still fail here — a network share or container-mounted volume that doesn't
                    // support native change notifications, or a permissions edge case. This runs
                    // inside a BackgroundService, whose unhandled exceptions stop the ENTIRE host by
                    // default (HostOptions.BackgroundServiceExceptionBehavior) — a live-reload
                    // convenience for ONE misbehaving path must not take the whole process down with
                    // it (code review on #705). Skip that path; every other configured path still
                    // gets a working watcher.
                    _logger.LogError(ex,
                        "Could not create a filesystem watcher for agent path {Path} — live reload is " +
                        "unavailable for this path; an explicit refresh or restart is still needed for " +
                        "changes under it", path);
                    continue;
                }

                watchers.Add(watcher);
            }

            _watchers = watchers;
            _watchedPaths = resolvedPaths;

            _logger.LogInformation("Agent manifest watcher active for {Count} path(s)", watchers.Count);
        }
    }

    private void OnManifestChanged(object sender, FileSystemEventArgs e) => ScheduleInvalidate();

    /// <summary>
    /// Handles a <see cref="FileSystemWatcher.Error"/> event — the internal event buffer overflowed
    /// or the underlying watch failed. The watcher that raised this is no longer reliably delivering
    /// events, so rather than trying to repair just that one, every watcher is disposed and rebuilt
    /// from the current configuration, and the cache is invalidated unconditionally since a change
    /// may already have been missed.
    /// </summary>
    internal void OnWatcherError(object? sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(),
            "Agent manifest watcher error (buffer overflow or watch failure) — invalidating and rebuilding watchers");

        _refresher.Invalidate();
        RetargetWatchers(_appConfig.CurrentValue.AI?.Agents, forceRebuild: true);
    }

    /// <summary>
    /// Resets the single shared debounce timer so a burst of filesystem events collapses into one
    /// invalidation, fired the configured quiet period after the LAST event in the burst.
    /// </summary>
    internal void ScheduleInvalidate()
    {
        var configuredMs = _appConfig.CurrentValue.AI?.Agents?.ChangeDebounceMilliseconds ?? 500;
        var clampedMs = Math.Clamp(configuredMs, 50, 30_000);
        if (clampedMs != configuredMs)
        {
            _logger.LogWarning(
                "AI:Agents:ChangeDebounceMilliseconds ({ConfiguredMs}) is outside the supported range " +
                "[50, 30000] — using {ClampedMs}ms instead",
                configuredMs, clampedMs);
        }

        var debounce = TimeSpan.FromMilliseconds(clampedMs);

        lock (_watchersLock)
        {
            if (_stopped)
                return;

            if (_debounceTimer is null)
            {
                _debounceTimer = _timeProvider.CreateTimer(
                    _ => FireInvalidate(),
                    state: null,
                    dueTime: debounce,
                    period: Timeout.InfiniteTimeSpan);
            }
            else
            {
                _debounceTimer.Change(debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void FireInvalidate()
    {
        _refresher.Invalidate();
        _logger.LogDebug("Agent manifest change detected; registry cache invalidated");
    }

    private void Shutdown()
    {
        _optionsChangeSubscription?.Dispose();
        _optionsChangeSubscription = null;

        lock (_watchersLock)
        {
            _stopped = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            DisposeWatchersNoLock();
        }
    }

    private void StopWatching()
    {
        lock (_watchersLock)
        {
            DisposeWatchersNoLock();
        }
    }

    /// <summary>Caller must hold <see cref="_watchersLock"/>.</summary>
    private void DisposeWatchersNoLock()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnManifestChanged;
            watcher.Changed -= OnManifestChanged;
            watcher.Deleted -= OnManifestChanged;
            watcher.Renamed -= OnManifestChanged;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }

        _watchers = [];
        _watchedPaths = [];
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        base.Dispose();
        Shutdown();
    }
}
