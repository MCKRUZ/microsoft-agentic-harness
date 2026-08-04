namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Provides ambient access to the active AG-UI run's output context — the event writer and the id of
/// the conversation thread that owns the run. Uses <see cref="AsyncLocal{T}"/> storage so the context
/// is scoped to the async execution context of the AG-UI run handler and flows into the tools it invokes.
/// </summary>
public interface IAgUiEventWriterAccessor
{
    /// <summary>Gets or sets the current AG-UI event writer. Null when no run is active.</summary>
    IAgUiEventWriter? Writer { get; set; }

    /// <summary>
    /// Gets or sets the conversation thread id of the active run. Null when no run is active. A
    /// blocking-proxy tool reads this to bind its pending call to the owning thread, so a tool result
    /// can only be completed by a caller who owns that same thread.
    /// </summary>
    string? ThreadId { get; set; }

    /// <summary>
    /// Gets or sets the identity of the caller who started the active run. Null when no run is active.
    /// </summary>
    /// <remarks>
    /// Travels beside <see cref="ThreadId"/> because anything writing to the thread now has to say who
    /// it is writing as. A tool invoked mid-run is several frames below the HTTP request and has no
    /// <c>ClaimsPrincipal</c> of its own; without this it would have to either invent an identity or
    /// be exempted from the ownership check, and an exemption is the hole the check exists to close.
    /// </remarks>
    string? CallerId { get; set; }
}

/// <summary>
/// Default implementation using <see cref="AsyncLocal{T}"/> for execution-context-scoped storage.
/// </summary>
public sealed class AgUiEventWriterAccessor : IAgUiEventWriterAccessor
{
    private static readonly AsyncLocal<IAgUiEventWriter?> _current = new();
    private static readonly AsyncLocal<string?> _threadId = new();
    private static readonly AsyncLocal<string?> _callerId = new();

    /// <inheritdoc />
    public IAgUiEventWriter? Writer
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <inheritdoc />
    public string? ThreadId
    {
        get => _threadId.Value;
        set => _threadId.Value = value;
    }

    /// <inheritdoc />
    public string? CallerId
    {
        get => _callerId.Value;
        set => _callerId.Value = value;
    }
}
