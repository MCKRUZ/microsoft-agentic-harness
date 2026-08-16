namespace Application.AI.Common.Interfaces.Sandbox;

/// <summary>
/// A long-lived, bidirectional sandboxed process/container session — the duplex counterpart to
/// <see cref="ISandboxExecutor"/>'s one-shot, run-to-completion contract. Used where a caller
/// needs to hold an open conversation with a sandboxed program across many messages (e.g. an
/// MCP stdio server for the length of a bundle run) rather than a single buffered
/// input/output exchange.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ISandboxExecutor"/>, a session produces no signed attestation: the
/// attestation model binds one complete input to one complete output, which a long-lived,
/// many-message session has no equivalent of. This is a deliberate, documented gap — see #371 —
/// not an oversight; a caller that needs per-message attestation must build it on top.
/// </para>
/// <para>
/// Disposing the session terminates the underlying process or container and releases every
/// resource it holds (Job Object handle, workspace directory, container). Implementations must
/// make disposal safe to call more than once and safe to call before <see cref="Completion"/>
/// has completed.
/// </para>
/// </remarks>
public interface ISandboxSession : IAsyncDisposable
{
    /// <summary>
    /// Stream to write session input to. Writes are forwarded to the sandboxed program as they
    /// happen — this is not buffered to completion the way <c>SandboxExecutionRequest.Input</c>
    /// is for a one-shot execution. Disposing the session closes this stream.
    /// </summary>
    Stream StandardInput { get; }

    /// <summary>
    /// Stream to read session output from. Bytes become available as the sandboxed program
    /// produces them, not drained to completion the way a one-shot result's <c>Output</c> is.
    /// </summary>
    Stream StandardOutput { get; }

    /// <summary>
    /// Completes when the underlying process/container exits on its own — crash, normal exit,
    /// or resource-limit termination — not when the caller disposes the session. A caller that
    /// wants to detect an unexpected exit while a conversation is still in progress should
    /// observe this task. Guaranteed to complete (never left pending forever, never faults
    /// silently) once the session is disposed.
    /// </summary>
    Task Completion { get; }
}
