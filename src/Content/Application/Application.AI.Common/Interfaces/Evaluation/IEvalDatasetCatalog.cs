namespace Application.AI.Common.Interfaces.Evaluation;

/// <summary>
/// Maps evaluation dataset <em>names</em> to the files they stand for, inside the host's configured
/// roots.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists so a filesystem path never has to cross the trust boundary.</strong> The eval
/// command takes paths, which is right for the CLI — a developer pointing the runner at a file on
/// their own machine. Exposing that same shape over HTTP would make every request a filesystem
/// reference, leaving one guard as the only thing between a caller and an arbitrary read. Names
/// cannot express "outside the roots", so the dangerous request stops being something the API can
/// describe rather than something it has to reject.
/// </para>
/// <para>
/// <strong>A name is an identifier, not a path fragment.</strong> Implementations must refuse anything
/// containing a directory separator, a drive qualifier, or a relative segment, and must not treat a
/// name as something to concatenate onto a root and hope. Resolution works by looking at what is
/// actually there and matching, which is why <see cref="ListNames"/> and <see cref="Resolve"/> can
/// never disagree about what exists.
/// </para>
/// </remarks>
public interface IEvalDatasetCatalog
{
    /// <summary>
    /// The dataset names this host will run, across every configured root.
    /// </summary>
    /// <returns>
    /// Distinct names in a stable order. Empty when no roots are configured — an unconfined host has
    /// no catalog to publish, and enumerating one would mean listing the filesystem.
    /// </returns>
    IReadOnlyList<string> ListNames();

    /// <summary>Resolves one dataset name to the absolute file path it stands for.</summary>
    /// <param name="name">The dataset name, as it appears in <see cref="ListNames"/>.</param>
    /// <returns>
    /// The absolute path, or <see langword="null"/> when the name is not one this host serves —
    /// including when it is malformed. The two are deliberately indistinguishable, for the same reason
    /// the path guard's refusals are: telling a caller which of their guesses was a <em>valid</em> name
    /// that simply does not exist turns the endpoint into an oracle.
    /// </returns>
    string? Resolve(string name);
}
