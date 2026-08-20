namespace Application.AI.Common.Services.Bundles;

/// <summary>
/// Ambient accessor that publishes the job id of the bundle run active for the current async flow, so
/// <c>McpConnectionManager</c> can give each run its own stdio MCP session instead of sharing one across
/// concurrent runs of the same staged bundle.
/// </summary>
/// <remarks>
/// <para>
/// Follows the same pattern as the host's other per-flow accessors
/// (<see cref="EphemeralAgentOverlayAccessor"/>, <c>CapabilityEnvelopeAccessor</c>,
/// <c>ToolAdmissionAccessor</c>): an <see cref="AsyncLocal{T}"/> the run path sets at the start of a
/// bundle run and clears in a <c>finally</c>, read at connection time by
/// <c>Infrastructure.AI.MCP.Services.McpConnectionManager</c>.
/// </para>
/// <para>
/// <strong>Absence is not a safe default for a bundle-owned stdio server.</strong> The MCP stdio
/// transport is single-session by design (its own SDK documents this as unsuitable for multi-tenant
/// sharing), so a caller resolving one with no run id armed must refuse rather than fall back to a
/// shared, unscoped session — the exact bug this accessor exists to close. Non-bundle and remote
/// bundle-owned servers never consult this accessor at all.
/// </para>
/// <para>
/// Prefer <see cref="Begin"/> to publish a run id: it restores the previous ambient value on dispose, so
/// nested or sequential runs on the same flow cannot leak one run's id into another's work.
/// </para>
/// </remarks>
public static class BundleRunIdAccessor
{
    private static readonly AsyncLocal<string?> s_current = new();

    /// <summary>The run id for the current async flow, or <see langword="null"/> when not inside a bundle run.</summary>
    public static string? Current => s_current.Value;

    /// <summary>
    /// Publishes <paramref name="runId"/> as the ambient run id for the current async flow and returns a
    /// handle that restores the previous ambient value when disposed. Use with <c>using</c> so the run id
    /// is guaranteed to be torn down when the run completes, even on exception.
    /// </summary>
    /// <param name="runId">The bundle run's job id; must not be null or empty.</param>
    public static IDisposable Begin(string runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        var previous = s_current.Value;
        s_current.Value = runId;
        return new RunIdScope(previous);
    }

    private sealed class RunIdScope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            s_current.Value = previous;
        }
    }
}
