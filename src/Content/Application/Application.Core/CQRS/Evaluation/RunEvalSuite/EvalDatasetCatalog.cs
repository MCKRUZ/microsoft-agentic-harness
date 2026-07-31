using Application.AI.Common.Interfaces.Evaluation;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Evaluation.RunEvalSuite;

/// <summary>
/// Default <see cref="IEvalDatasetCatalog"/>: the datasets are the files sitting at the top level of
/// the configured roots, and their names are those files' names without the extension.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Resolution is by enumeration, not by concatenation.</strong> The tempting implementation is
/// <c>Path.Combine(root, name + ".yaml")</c> with a validity check on <c>name</c> — and that is a
/// path-building routine wearing a name-shaped interface, one missed character class away from
/// traversal. Listing what is actually in the root and matching against it cannot construct a path
/// the caller influenced, so there is no character class to get wrong.
/// </para>
/// <para>
/// <strong>Top level only, deliberately.</strong> Datasets in subdirectories would need names that
/// carry structure, and a name with structure is a path with extra steps — it would have to be split,
/// rejoined, and validated, which is the concatenation problem again. A flat namespace per root keeps
/// a name an opaque identifier. The cost is that operators organise by root rather than by folder,
/// which is a real constraint and is documented on <c>EvaluationConfig.DatasetRoots</c>.
/// </para>
/// <para>
/// <strong>No catalog without roots.</strong> An unconfined host — the CLI's shipped default — returns
/// nothing rather than enumerating whatever directory it happens to be running from. Listing is a
/// disclosure, and there is no bounded thing to disclose until an operator says what the bounds are.
/// </para>
/// </remarks>
public sealed class EvalDatasetCatalog : IEvalDatasetCatalog
{
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly IEvalDatasetPathGuard _guard;
    private readonly ILogger<EvalDatasetCatalog> _logger;

    /// <summary>Initializes the catalog.</summary>
    /// <param name="config">Supplies the dataset roots, read per call so a new root needs no restart.</param>
    /// <param name="guard">
    /// Confines every resolved path. Applied here as well as in the handler on purpose: this class
    /// decides what is reachable by name, and it should not be able to publish a name whose file the
    /// handler will then refuse.
    /// </param>
    /// <param name="logger">Records name collisions across roots, which are otherwise silent.</param>
    public EvalDatasetCatalog(
        IOptionsMonitor<AppConfig> config,
        IEvalDatasetPathGuard guard,
        ILogger<EvalDatasetCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _guard = guard;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListNames() => [.. Discover().Keys.OrderBy(n => n, StringComparer.Ordinal)];

    /// <inheritdoc />
    public string? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // A name is an identifier. Anything that could steer resolution — a separator, a drive
        // qualifier, a relative segment — is refused before it is compared, so a name that looks like
        // a path never gets as far as being looked up. Enumeration would not honour it anyway; this
        // just makes the intent explicit rather than incidental.
        if (name.AsSpan().IndexOfAny('/', '\\', ':') >= 0 || name is "." or "..")
            return null;

        return Discover().TryGetValue(name, out var path) ? path : null;
    }

    /// <summary>
    /// Builds the name-to-path map from what is currently on disk under the configured roots.
    /// </summary>
    /// <remarks>
    /// Not cached. The roots are a small, operator-curated set of directories, and a stale catalog
    /// would report a dataset that has since been removed — a caller then submits a run that fails at
    /// load time, having already been admitted and queued. Reading the directory per call is cheap
    /// next to the model spend of the run it authorizes.
    /// </remarks>
    private Dictionary<string, string> Discover()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var roots = _config.CurrentValue.AI.Evaluation.DatasetRoots ?? [];

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(Path.GetFullPath(root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException
                                          or PathTooLongException)
            {
                // A root that cannot be read contributes nothing. It is not an error for the caller:
                // the effect is that its datasets are absent, which is exactly what an absent name
                // already means.
                _logger.LogWarning(ex, "Evaluation dataset root {Root} could not be read.", root);
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                // The guard has the final say on every path this class hands out, so the catalog can
                // never publish a name the handler would then refuse to load.
                var decision = _guard.Resolve(file);
                if (!decision.IsAllowed)
                    continue;

                if (found.TryGetValue(name, out var existing))
                {
                    if (!string.Equals(existing, decision.CanonicalPath, StringComparison.Ordinal))
                    {
                        _logger.LogWarning(
                            "Evaluation dataset name {Name} is defined in more than one root; keeping "
                            + "the first and ignoring {Ignored}.",
                            name,
                            file);
                    }

                    continue;
                }

                found[name] = decision.CanonicalPath!;
            }
        }

        return found;
    }
}
