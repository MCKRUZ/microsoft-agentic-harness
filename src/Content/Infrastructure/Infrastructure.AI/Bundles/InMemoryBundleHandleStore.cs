using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Bundles;
using Domain.AI.Bundles;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// In-memory <see cref="IBundleHandleStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>,
/// with a sliding TTL per handle and run-pinning so a staging directory is never deleted while a run is
/// executing against it. Owns the on-disk lifetime of every staged bundle it holds: it deletes the staging
/// directory on explicit removal, on TTL expiry (via the cleanup sweeper), and on host shutdown (disposal).
/// </summary>
/// <remarks>
/// <para>
/// A dictionary + <see cref="TimeProvider"/>-driven sweep is used deliberately over an
/// <c>IMemoryCache</c> post-eviction callback. The load-bearing requirement is <em>guaranteed</em> temp-dir
/// cleanup with a run in flight never losing its directory: an in-use entry must be pinned against eviction,
/// and eviction timing must be deterministic and testable. <c>IMemoryCache</c> cannot pin an in-use entry
/// and fires its callbacks lazily; explicit refcounting plus a deterministic sweep gives exactly the
/// guarantee the security posture requires.
/// </para>
/// <para>
/// Each handle's mutable state (expiry, lease count, removal flag) is guarded by a lock on its entry, so
/// concurrent reads, run acquisitions, releases, and sweeps agree on whether — and when — a directory may
/// be deleted. The map itself is concurrent; entries are removed with a compare-and-remove so a sweep can
/// never delete an entry a concurrent acquisition has just revived.
/// </para>
/// </remarks>
public sealed class InMemoryBundleHandleStore : IBundleHandleStore, IDisposable
{
    private sealed class HandleEntry(StagedBundle bundle, string ownerId)
    {
        public StagedBundle Bundle { get; } = bundle;
        public string OwnerId { get; } = ownerId;
        public DateTimeOffset ExpiresAt { get; set; }
        public int LeaseCount { get; set; }
        public bool Removed { get; set; }
    }

    private readonly ConcurrentDictionary<string, HandleEntry> _entries = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<InMemoryBundleHandleStore> _logger;
    private bool _disposed;

    /// <summary>Initializes a new <see cref="InMemoryBundleHandleStore"/>.</summary>
    public InMemoryBundleHandleStore(
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<InMemoryBundleHandleStore> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _time = time;
        _logger = logger;
    }

    private TimeSpan Ttl => _config.CurrentValue.AI.BundleExecution.HandleTtl;

    /// <inheritdoc />
    public string Register(StagedBundle bundle, string ownerId)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);

        var handle = bundle.BundleId;
        var entry = new HandleEntry(bundle, ownerId) { ExpiresAt = _time.GetUtcNow() + Ttl };
        _entries[handle] = entry;
        return handle;
    }

    /// <inheritdoc />
    public string? GetOwner(string handle)
    {
        ArgumentException.ThrowIfNullOrEmpty(handle);
        if (!_entries.TryGetValue(handle, out var entry))
            return null;

        lock (entry)
        {
            return IsLive(entry) ? entry.OwnerId : null;
        }
    }

    /// <inheritdoc />
    public StagedBundle? TryGet(string handle)
    {
        ArgumentException.ThrowIfNullOrEmpty(handle);
        if (!_entries.TryGetValue(handle, out var entry))
            return null;

        lock (entry)
        {
            if (!IsLive(entry))
                return null;

            entry.ExpiresAt = _time.GetUtcNow() + Ttl;
            return entry.Bundle;
        }
    }

    /// <inheritdoc />
    public IBundleHandleLease? Acquire(string handle)
    {
        ArgumentException.ThrowIfNullOrEmpty(handle);
        if (!_entries.TryGetValue(handle, out var entry))
            return null;

        lock (entry)
        {
            if (!IsLive(entry))
                return null;

            entry.LeaseCount++;
            entry.ExpiresAt = _time.GetUtcNow() + Ttl;
            return new HandleLease(this, handle, entry);
        }
    }

    /// <inheritdoc />
    public bool Remove(string handle)
    {
        ArgumentException.ThrowIfNullOrEmpty(handle);
        if (!_entries.TryGetValue(handle, out var entry))
            return false;

        var deletePath = default(string);
        lock (entry)
        {
            entry.Removed = true;
            if (entry.LeaseCount == 0)
                deletePath = entry.Bundle.StagedRootDirectory;
        }

        if (deletePath is not null)
            EvictAndDelete(handle, entry, deletePath);

        return true;
    }

    /// <inheritdoc />
    public int SweepExpired()
    {
        var now = _time.GetUtcNow();
        var evicted = 0;

        foreach (var (handle, entry) in _entries)
        {
            var deletePath = default(string);
            lock (entry)
            {
                if (entry.LeaseCount == 0 && (entry.Removed || now >= entry.ExpiresAt))
                {
                    entry.Removed = true;
                    deletePath = entry.Bundle.StagedRootDirectory;
                }
            }

            if (deletePath is not null && EvictAndDelete(handle, entry, deletePath))
                evicted++;
        }

        return evicted;
    }

    /// <summary>
    /// Releases a run's pin on a handle: decrements the lease count, slides the expiry forward, and — if the
    /// handle was removed while the run held it — deletes its staging directory now that no run remains.
    /// </summary>
    private void ReleaseLease(string handle, HandleEntry entry)
    {
        var deletePath = default(string);
        lock (entry)
        {
            if (entry.LeaseCount > 0)
                entry.LeaseCount--;

            entry.ExpiresAt = _time.GetUtcNow() + Ttl;

            if (entry.LeaseCount == 0 && entry.Removed)
                deletePath = entry.Bundle.StagedRootDirectory;
        }

        if (deletePath is not null)
            EvictAndDelete(handle, entry, deletePath);
    }

    /// <summary>
    /// Removes the exact entry from the map (compare-and-remove, so a revived entry is never clobbered) and
    /// deletes its staging directory. Deletion failures are logged, never thrown — a run has already
    /// finished by the time we delete, so a transient I/O failure must not crash the sweeper or a caller.
    /// </summary>
    private bool EvictAndDelete(string handle, HandleEntry entry, string stagedRootDirectory)
    {
        if (!_entries.TryRemove(new KeyValuePair<string, HandleEntry>(handle, entry)))
            return false;

        DeleteDirectorySafely(stagedRootDirectory);
        return true;
    }

    private void DeleteDirectorySafely(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete staged bundle directory {StagedRootDirectory}", path);
        }
    }

    private bool IsLive(HandleEntry entry) =>
        !entry.Removed && (entry.LeaseCount > 0 || _time.GetUtcNow() < entry.ExpiresAt);

    /// <summary>
    /// Deletes remaining staging directories on host shutdown — the final guaranteed-cleanup path, so a clean
    /// stop never leaves extracted bundles on disk. A directory still pinned by an in-flight run is left in
    /// place: by the time disposal runs the hosted services have stopped, so a lingering lease means a run
    /// ignored the shutdown token past the shutdown timeout and is still reading its skills from disk —
    /// deleting under it would corrupt that run, and the process is exiting regardless, so leaving the (rare)
    /// orphaned directory is the safe choice.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var (_, entry) in _entries)
        {
            lock (entry)
            {
                if (entry.LeaseCount == 0)
                    DeleteDirectorySafely(entry.Bundle.StagedRootDirectory);
            }
        }

        _entries.Clear();
    }

    private sealed class HandleLease(InMemoryBundleHandleStore store, string handle, HandleEntry entry)
        : IBundleHandleLease
    {
        private bool _disposed;

        public StagedBundle Bundle => entry.Bundle;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            store.ReleaseLease(handle, entry);
        }
    }
}
