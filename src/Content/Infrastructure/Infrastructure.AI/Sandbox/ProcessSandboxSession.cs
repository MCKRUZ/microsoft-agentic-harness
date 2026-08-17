using System.Diagnostics;
using Application.AI.Common.Interfaces.Sandbox;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// A <see cref="Process"/>-backed <see cref="ISandboxSession"/>. NOT a containment boundary —
/// see the remarks on <see cref="ProcessSandboxSessionFactory"/>. Enforces
/// <see cref="Domain.AI.Sandbox.SandboxSessionRequest.MaxSessionDuration"/> as a hard ceiling:
/// the process is killed if the caller has not disposed the session by then.
/// </summary>
public sealed class ProcessSandboxSession : ISandboxSession
{
    /// <summary>
    /// Bound on how long <see cref="DisposeAsync"/> waits for the process to actually exit after
    /// <see cref="ProcessSandboxLaunchPreparer.KillProcess"/> — mirrors
    /// <c>DockerContainerLaunchPreparer</c>'s <c>CleanupTimeoutSeconds</c>-bounded removal, for
    /// the same reason: if <c>Kill(entireProcessTree: true)</c> fails to actually terminate a
    /// hung/zombie child, an unbounded re-wait would block session teardown — and by extension
    /// the caller tearing it down — forever.
    /// </summary>
    private static readonly TimeSpan PostKillWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly Process _process;
    private readonly ProcessSandboxLaunchPreparer _launchPreparer;
    private readonly string _workspaceDir;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly Task _completion;
    private readonly Task _stderrDrain;
    private int _disposed;

    internal ProcessSandboxSession(
        Process process,
        ProcessSandboxLaunchPreparer launchPreparer,
        string workspaceDir,
        TimeSpan maxSessionDuration,
        ILogger logger)
    {
        _process = process;
        _launchPreparer = launchPreparer;
        _workspaceDir = workspaceDir;
        _logger = logger;
        _lifetimeCts = new CancellationTokenSource(maxSessionDuration);
        StandardInput = process.StandardInput.BaseStream;
        StandardOutput = process.StandardOutput.BaseStream;
        // The process is launched with RedirectStandardError=true (ProcessSandboxLaunchPreparer.
        // StartProcess), so something must continuously drain it: an OS pipe has a bounded buffer,
        // and once it fills with nobody reading, the child's writes to stderr block — hanging the
        // sandboxed program the first time it logs more than a few KB of diagnostics.
        _stderrDrain = DrainStandardErrorAsync();
        _completion = WaitForExitAsync();
    }

    /// <inheritdoc />
    public Stream StandardInput { get; }

    /// <inheritdoc />
    public Stream StandardOutput { get; }

    /// <inheritdoc />
    public Task Completion => _completion;

    private async Task DrainStandardErrorAsync()
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                // Best-effort cancellation: an OS pipe read that's genuinely blocked in native
                // code is not guaranteed to unblock just because this token fires — DisposeAsync
                // does not rely on that guarantee (see the WaitAsync race there) but passing it
                // still helps the common case where the stream itself has already been closed.
                var read = await _process.StandardError.ReadAsync(buffer.AsMemory(), _lifetimeCts.Token);
                if (read == 0)
                    break;

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Sandboxed process {ProcessId} stderr: {Chunk}", _process.Id, new string(buffer, 0, read));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stderr drain for process {ProcessId} ended", _process.Id);
        }
    }

    private async Task WaitForExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Either MaxSessionDuration elapsed or DisposeAsync requested an early stop. Either
            // way the caller-visible Completion task must still finish (interface contract:
            // "guaranteed to complete"), so kill and re-wait unconditionally rather than
            // propagating the cancellation. Bounded, not indefinite: see PostKillWaitTimeout.
            _launchPreparer.KillProcess(_process);
            try
            {
                using var postKillCts = new CancellationTokenSource(PostKillWaitTimeout);
                await _process.WaitForExitAsync(postKillCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Process {ProcessId} did not exit within {Timeout} of being killed — treating session as ended anyway",
                    _process.Id, PostKillWaitTimeout);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected failure waiting for sandboxed process {ProcessId} to exit", _process.Id);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Captured before any disposal below: SafeDispose's own log line needs an identifier for
        // this session, and reading Process.Id after the process handle it disposes is closed is
        // not something to rely on.
        var processIdForLogging = _process.Id.ToString();

        await _lifetimeCts.CancelAsync();
        await _completion;

        // Not a plain await: a stderr read blocked in native code is not guaranteed to unblock
        // just because the token above fired (a known limitation for OS-pipe-backed streams), and
        // this method exists specifically so a stuck drain cannot silently defeat the bounded
        // teardown _completion already enforces. Degrades to a warning and proceeds with cleanup
        // rather than hanging forever; the drain task, if still running, keeps its own internal
        // catch and simply completes later once the process handle below is disposed out from
        // under it. WaitAsync (not WhenAny+Delay) disarms its own timer the moment the drain
        // completes, instead of leaving an armed Task.Delay reachable for the full timeout on the
        // normal, non-stuck teardown path.
        try
        {
            await _stderrDrain.WaitAsync(PostKillWaitTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Stderr drain for process {ProcessId} did not finish within {Timeout} of teardown — proceeding with cleanup anyway",
                _process.Id, PostKillWaitTimeout);
        }

        // Both calls are internally safe (Release() is a documented no-op for an unknown id;
        // CleanupWorkspace catches and logs its own failures) — no guard needed before the
        // stream/handle disposals below, which are individually guarded so one failure can't
        // skip releasing the rest.
        _launchPreparer.ReleaseResourceLimiter(_process.Id);
        _launchPreparer.CleanupWorkspace(_workspaceDir);

        SafeDispose(StandardInput, "stdin stream", processIdForLogging);
        SafeDispose(StandardOutput, "stdout stream", processIdForLogging);
        SafeDispose(_process, "process handle", processIdForLogging);
        SafeDispose(_lifetimeCts, "lifetime cancellation token source", processIdForLogging);
    }

    private void SafeDispose(IDisposable disposable, string what, string processIdForLogging) =>
        SandboxWorkspace.SafeDispose(disposable, what, processIdForLogging, _logger);
}
