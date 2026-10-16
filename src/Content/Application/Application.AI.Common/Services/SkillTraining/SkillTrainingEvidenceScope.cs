namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// The one step's evidence folder the current proposer session may read, published for the duration of
/// that session (#843).
/// </summary>
/// <remarks>
/// <para>
/// The <c>evidence_reader</c> tool is confined to the evidence root, but the root holds one folder per
/// step, and concurrent runs and trees left behind by a crash share it. Confinement to the root alone
/// would let a proposer session, steered by text inside a trajectory, list the root and read another
/// run's evidence. This narrows a session to its own folder: the tool refuses any path outside the
/// folder published here, and refuses everything when none is published.
/// </para>
/// <para>
/// Ambient (<see cref="AsyncLocal{T}"/>) because the tool is a singleton called by the agent framework far
/// below the code that knows which step it is serving. It is published around one awaited call, not
/// around a stream: ambient state set inside an async iterator does not survive a <c>yield</c>, and
/// nothing here is one.
/// </para>
/// </remarks>
public static class SkillTrainingEvidenceScope
{
    private static readonly AsyncLocal<string?> CurrentDirectory = new();

    /// <summary>The folder the current session may read, or <see langword="null"/> when none is active.</summary>
    public static string? Current => CurrentDirectory.Value;

    /// <summary>Publishes <paramref name="directory"/> as the readable folder until the result is disposed.</summary>
    /// <param name="directory">The absolute folder of this step's evidence tree.</param>
    /// <returns>A handle that restores the previous value.</returns>
    public static IDisposable Begin(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var previous = CurrentDirectory.Value;
        CurrentDirectory.Value = Path.GetFullPath(directory);
        return new Restore(previous);
    }

    /// <summary>
    /// Whether <paramref name="path"/>, once resolved, is the active folder or inside it. False when no
    /// folder is active.
    /// </summary>
    /// <param name="path">A path a tool call asked for.</param>
    public static bool Allows(string path)
    {
        var scope = CurrentDirectory.Value;
        if (scope is null)
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = scope.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(root, comparison)
            || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || full.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => CurrentDirectory.Value = previous;
    }
}
