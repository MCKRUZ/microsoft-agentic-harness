namespace Application.AI.Common.Exceptions;

/// <summary>
/// A first-party tool reports a name other than the key it is registered under (#888). Such a tool is refused
/// wherever a tool is built by key: every grant, deny rule and manifest names a tool by one string, and that string
/// must mean one tool whichever way it is read.
/// </summary>
/// <remarks>
/// Raised by <c>KeyedToolResolution.Resolve</c>, the only place a keyed tool is built. Each caller treats the tool as
/// unavailable, exactly as it treats one that cannot be constructed in this host. The fix is to rename one of the two
/// so they agree (case does not matter).
/// </remarks>
public sealed class ToolNameMismatchException : InvalidOperationException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="key">The key the tool is registered under.</param>
    /// <param name="name">The name the tool reports.</param>
    public ToolNameMismatchException(string key, string name)
        : base($"The tool registered under key '{key}' reports its name as '{name}'. A tool's registration key and " +
               "its name must be the same (case aside), so that a grant or deny rule naming it cannot also mean " +
               "another tool. The tool is unavailable until the two agree.")
    {
        Key = key;
        Name = name;
    }

    /// <summary>Gets the key the tool is registered under.</summary>
    public string Key { get; }

    /// <summary>Gets the name the tool reports.</summary>
    public string Name { get; }
}
