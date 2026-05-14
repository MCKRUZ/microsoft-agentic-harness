namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Provides access to the current AG-UI event writer for the active run.
/// Uses <see cref="AsyncLocal{T}"/> storage so the writer is scoped to the
/// async execution context of the AG-UI run handler.
/// </summary>
public interface IAgUiEventWriterAccessor
{
    /// <summary>Gets or sets the current AG-UI event writer. Null when no run is active.</summary>
    IAgUiEventWriter? Writer { get; set; }
}

/// <summary>
/// Default implementation using <see cref="AsyncLocal{T}"/> for execution-context-scoped storage.
/// </summary>
public sealed class AgUiEventWriterAccessor : IAgUiEventWriterAccessor
{
    private static readonly AsyncLocal<IAgUiEventWriter?> _current = new();

    /// <inheritdoc />
    public IAgUiEventWriter? Writer
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
