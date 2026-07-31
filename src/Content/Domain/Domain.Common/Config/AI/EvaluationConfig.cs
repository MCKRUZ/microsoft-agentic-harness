namespace Domain.Common.Config.AI;

/// <summary>
/// Server-side evaluation configuration: where dataset files may be read from, and the ceilings on
/// what one run may spend. Bound from <c>AppConfig:AI:Evaluation</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Evaluation was built for the EvalRunner CLI, where the caller is
/// a developer on their own machine and every input is trusted. The ceilings that keep a run bounded
/// (parallelism 1–128, a sensible case count) lived only in that CLI's argument parsing, and
/// <c>RunEvalSuiteCommand</c> accepted raw filesystem paths validated solely as non-empty. Both are
/// correct for a local tool and neither survives contact with an HTTP caller: unbounded parallelism
/// is a cost and rate-limit amplifier, and a raw path is an arbitrary-file-read probe.
/// </para>
/// <para>
/// <strong>Confinement is opt-in, and that is deliberate.</strong> When <see cref="DatasetRoots"/> is
/// empty, dataset paths are unconfined — which preserves the CLI workflow of pointing the runner at a
/// file anywhere on disk. When it is non-empty, <em>every</em> dispatch of the command is confined,
/// regardless of who dispatched it. The enforcement therefore lives at the handler, not at each
/// caller: a check the HTTP surface has to remember to perform is a check that will eventually be
/// forgotten. A host exposing evaluation over HTTP must configure roots, and the HTTP surface refuses
/// to run without them rather than falling back to the unconfined behaviour.
/// </para>
/// </remarks>
public class EvaluationConfig
{
    /// <summary>
    /// Whether this host wires the evaluation framework at all. Off by default: the framework brings
    /// a YAML loader, twelve metric singletons, three reporters, and the harness agent invoker, and a
    /// host that never evaluates should not pay for them on every cold start.
    /// </summary>
    /// <remarks>
    /// Turning this on in a host that serves untrusted callers <strong>requires</strong>
    /// <see cref="DatasetRoots"/>. The host refuses to start otherwise rather than falling back to the
    /// unconfined default, which is correct for the CLI and wrong for anything reachable over a
    /// network.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Directories that dataset files may be read from. Empty (the default) means unconfined, which is
    /// correct only for a trusted local caller such as the EvalRunner CLI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paths are compared after full canonicalisation, so <c>..</c> traversal and symbolic links that
    /// point outside a root are rejected rather than resolved through — on every path segment, not just
    /// the last one.
    /// </para>
    /// <para>
    /// <strong>Configure absolute paths.</strong> A relative entry is resolved against the process's
    /// current working directory, which is not reliably the content root: a service unit, a Windows
    /// service, or a container whose <c>WORKDIR</c> differs will each resolve it somewhere else, and the
    /// resulting root would confine to a directory nobody intended.
    /// </para>
    /// <para>
    /// Blank and whitespace-only entries are dropped. Once a host has started with roots configured,
    /// removing them at runtime does not restore unconfined reads — it refuses everything instead.
    /// </para>
    /// <para>
    /// <strong>Deployment invariant: a root must not be writable by anyone the host would not already
    /// let read arbitrary files.</strong> This is not a code guarantee and cannot be made one. Anyone who
    /// can write inside a root can place a hard link there (which has no target to resolve and is
    /// indistinguishable from an ordinary file), or swap a file for a symbolic link between the moment
    /// the path is checked and the moment it is opened. Confinement bounds which <em>paths</em> a caller
    /// may name; it cannot bound what the contents of a writable directory turn out to be.
    /// </para>
    /// </remarks>
    public List<string> DatasetRoots { get; set; } = [];

    /// <summary>
    /// Maximum number of dataset files one run may load. Guards against a caller submitting a large
    /// list to multiply the run's cost.
    /// </summary>
    /// <value>Default: 10.</value>
    public int MaxDatasetsPerRun { get; set; } = 10;

    /// <summary>
    /// Maximum number of case <em>executions</em> one run may perform — the loaded case count multiplied
    /// by <c>Repeats</c>, which is what the run actually costs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counting cases alone would leave the ceiling trivially bypassable: 500 cases at the default 50
    /// repeats is 25,000 governed agent turns plus their judge calls, all from a request that passed a
    /// "500" limit. Repeats multiply spend, so they belong inside the number being capped.
    /// </para>
    /// <para>
    /// Counted from the datasets as loaded, <em>before</em> any tag filter narrows them. That
    /// over-estimates a filtered run, and deliberately so: the error direction refuses a run that might
    /// have fitted rather than admitting one that does not.
    /// </para>
    /// <para>
    /// A run that would exceed this is refused rather than truncated — silently evaluating a subset
    /// would report a pass rate for a suite that never ran.
    /// </para>
    /// </remarks>
    /// <value>Default: 500.</value>
    public int MaxCaseExecutionsPerRun { get; set; } = 500;

    /// <summary>
    /// Maximum size of a single dataset file, in bytes. Checked before the file is opened.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxCaseExecutionsPerRun"/> bounds what a run may <em>execute</em>, but it can only be
    /// evaluated once cases exist — which means parsing. Without a size cap the parse itself is the
    /// attack: a caller names a file large enough to exhaust memory and the run is refused only
    /// afterwards, having already paid the cost. This bounds the work done before that decision.
    /// </remarks>
    /// <value>Default: 5 MiB — far above any realistic hand-authored suite.</value>
    public long MaxDatasetBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Ceiling on <c>EvalRunOptions.Repeats</c>. Each repeat re-invokes every case and its LLM judge,
    /// so cost scales linearly with this.
    /// </summary>
    /// <value>Default: 50, matching the validator's long-standing bound.</value>
    public int MaxRepeats { get; set; } = 50;

    /// <summary>
    /// Ceiling on <c>EvalRunOptions.Parallelism</c>. Previously enforced only by the EvalRunner CLI's
    /// argument parsing, so any other dispatcher could request unbounded concurrency against the
    /// model provider.
    /// </summary>
    /// <value>Default: 128, matching the CLI's long-standing bound.</value>
    public int MaxParallelism { get; set; } = 128;
}
