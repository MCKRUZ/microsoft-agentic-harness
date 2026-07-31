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

    /// <summary>
    /// Resolves a whole set of names to the files they stand for, against one view of the roots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The set, not one name at a time.</strong> Resolving is not free: an implementation
    /// answers by enumerating — this interface requires it — so it walks every configured root and
    /// confines every file it finds. Asking name by name repeats that walk once per name.
    /// </para>
    /// <para>
    /// It is also the more correct question. Name-at-a-time resolution reads the filesystem at as many
    /// instants as there are names, so a run could be admitted against a set of files that never
    /// existed together.
    /// </para>
    /// <para>
    /// <strong>It answers "which one is missing", not just "here is what I found".</strong> Every
    /// caller needs the same thing — all of them or none, because a suite silently evaluated without
    /// one of its datasets reports a pass rate for something that never ran. Returning a partial map
    /// left each caller to rediscover that rule, and both of them wrote the same three lines to do it.
    /// </para>
    /// </remarks>
    /// <param name="names">The names to resolve. Duplicates are permitted.</param>
    EvalDatasetResolution Resolve(IReadOnlyList<string> names);
}

/// <summary>
/// The outcome of resolving a set of dataset names: every file, or the first name that is not one
/// this host serves.
/// </summary>
/// <remarks>
/// The missing name is carried back so a caller can say which one it was. That is safe to disclose —
/// it is a name the caller supplied, and it reveals nothing about the filesystem. It says nothing
/// about <em>why</em>: an unknown name and a malformed one are the same answer, for the same reason
/// the path guard's refusals do not distinguish forbidden from absent.
/// </remarks>
public sealed record EvalDatasetResolution
{
    /// <summary>
    /// The absolute paths, in the order the names were given. Empty unless every name resolved.
    /// </summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>The first name that did not resolve, or <see langword="null"/> when all of them did.</summary>
    public string? MissingName { get; init; }

    /// <summary>Whether every name resolved.</summary>
    public bool IsComplete => MissingName is null;

    /// <summary>Every name resolved.</summary>
    /// <param name="paths">The resolved paths, in the order the names were given.</param>
    public static EvalDatasetResolution Complete(IReadOnlyList<string> paths) => new() { Paths = paths };

    /// <summary>One name did not resolve, so the set is unusable.</summary>
    /// <param name="missingName">The name this host does not serve.</param>
    public static EvalDatasetResolution Missing(string missingName) =>
        new() { MissingName = missingName };
}
