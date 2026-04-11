namespace Domain.Common.Config.MetaHarness;

/// <summary>
/// Configuration for the meta-harness optimization loop.
/// Binds to <c>AppConfig.MetaHarness</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Controls iteration count, evaluation tasks, trace output, and proposer behavior.
/// Each property has an inline default suitable for local development; override in
/// appsettings.json for production or CI environments.
/// </para>
/// <para>
/// <strong>Mutable setters are required by <c>IOptionsMonitor&lt;T&gt;</c> binding.</strong>
/// Treat instances as read-only after DI setup. Do not mutate at runtime.
/// </para>
/// </remarks>
public class MetaHarnessConfig
{
    /// <summary>
    /// Gets or sets the root path for all trace output directories.
    /// Each optimization run and candidate evaluation writes trace files beneath this path.
    /// Relative paths are resolved against the working directory at runtime.
    /// </summary>
    /// <value>Default: <c>"traces"</c>.</value>
    public string TraceDirectoryRoot { get; set; } = "traces";

    /// <summary>
    /// Gets or sets the maximum number of propose-evaluate iterations per optimization run.
    /// The loop exits early if no improvement is found within this limit.
    /// </summary>
    /// <value>Default: <c>10</c>.</value>
    public int MaxIterations { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum number of eval tasks sampled per candidate evaluation.
    /// A random subset of size <c>SearchSetSize</c> is drawn from the full task pool.
    /// </summary>
    /// <value>Default: <c>50</c>.</value>
    public int SearchSetSize { get; set; } = 50;

    /// <summary>
    /// Gets or sets the minimum pass-rate delta required for a candidate to be considered
    /// an improvement over the current best. Candidates that improve by less than this
    /// threshold are treated as ties and subject to cost tie-breaking.
    /// </summary>
    /// <value>Default: <c>0.01</c> (1% improvement).</value>
    public double ScoreImprovementThreshold { get; set; } = 0.01;

    /// <summary>
    /// Gets or sets whether the best candidate is automatically applied to the live harness
    /// after each optimization run. When <c>false</c>, the proposed changes are written to
    /// the <c>_proposed/</c> output directory only.
    /// </summary>
    /// <value>Default: <c>false</c>.</value>
    public bool AutoPromoteOnImprovement { get; set; } = false;

    /// <summary>
    /// Gets or sets the path to the directory containing evaluation task JSON files.
    /// Each <c>.json</c> file in this directory is loaded as an <c>EvalTask</c> record.
    /// Relative paths are resolved against the working directory at runtime.
    /// </summary>
    /// <value>Default: <c>"eval-tasks"</c>.</value>
    public string EvalTasksPath { get; set; } = "eval-tasks";

    /// <summary>
    /// Gets or sets the optional path to a seed harness snapshot used as the first candidate.
    /// When empty, the optimization loop seeds from the currently active harness configuration.
    /// </summary>
    /// <value>Default: <c>""</c> (use active configuration).</value>
    public string SeedCandidatePath { get; set; } = "";

    /// <summary>
    /// Gets or sets the maximum number of eval tasks that may run in parallel.
    /// Set to <c>1</c> for sequential execution, which is the default to avoid
    /// overwhelming shared AI model rate limits during evaluation.
    /// </summary>
    /// <value>Default: <c>1</c>.</value>
    public int MaxEvalParallelism { get; set; } = 1;

    /// <summary>
    /// Gets or sets the LLM sampling temperature used during evaluation runs.
    /// A value of <c>0.0</c> produces deterministic, reproducible eval results.
    /// </summary>
    /// <value>Default: <c>0.0</c>.</value>
    public double EvaluationTemperature { get; set; } = 0.0;

    /// <summary>
    /// Gets or sets an optional model deployment override for evaluation runs.
    /// When <c>null</c>, the evaluation agent uses the default model from
    /// <c>AppConfig.AI.AgentFramework.DefaultDeployment</c>.
    /// </summary>
    /// <value>Default: <c>null</c> (use default deployment).</value>
    public string? EvaluationModelVersion { get; set; }

    /// <summary>
    /// Gets or sets the list of <c>AppConfig</c> key paths to include when taking
    /// a harness configuration snapshot. Only keys matching these paths are captured;
    /// secret keys are always excluded regardless of this list.
    /// </summary>
    /// <remarks>
    /// Uses <c>IReadOnlyList&lt;string&gt;</c> consistent with the project's immutability convention
    /// (<see cref="Domain.Common.Config.Infrastructure.FileSystemConfig.AllowedBasePaths"/>).
    /// This property cannot be overridden via <c>appsettings.json</c>; configure it in code.
    /// </remarks>
    /// <value>Default: empty (no config keys snapshotted).</value>
    public IReadOnlyList<string> SnapshotConfigKeys { get; set; } = [];

    /// <summary>
    /// Gets or sets the list of config key substrings that are never included in harness
    /// snapshots, even when matched by <see cref="SnapshotConfigKeys"/>. Protects API keys,
    /// passwords, connection strings, and other sensitive values from being persisted to disk.
    /// </summary>
    /// <remarks>
    /// Uses <c>IReadOnlyList&lt;string&gt;</c> consistent with the project's immutability convention.
    /// This property cannot be overridden via <c>appsettings.json</c>; configure it in code.
    /// </remarks>
    /// <value>Default: <c>["Key", "Secret", "Token", "Password", "ConnectionString"]</c>.</value>
    public IReadOnlyList<string> SecretsRedactionPatterns { get; set; } =
        ["Key", "Secret", "Token", "Password", "ConnectionString"];

    /// <summary>
    /// Gets or sets the maximum size in kilobytes for per-call full payload artifacts.
    /// Payloads exceeding this limit are truncated before being written to the trace directory.
    /// </summary>
    /// <value>Default: <c>512</c> KB.</value>
    public int MaxFullPayloadKB { get; set; } = 512;

    /// <summary>
    /// Gets or sets the maximum number of optimization run directories to retain on disk.
    /// Older runs beyond this limit are deleted to manage storage.
    /// Set to <c>0</c> for unlimited retention.
    /// </summary>
    /// <value>Default: <c>20</c>.</value>
    public int MaxRunsToKeep { get; set; } = 20;

    /// <summary>
    /// Gets or sets whether the proposer agent is permitted to execute restricted shell commands
    /// via the <c>RestrictedSearchTool</c>. Disabled by default as an opt-in security boundary.
    /// Enable only in controlled environments where the proposer is trusted.
    /// </summary>
    /// <value>Default: <c>false</c>.</value>
    public bool EnableShellTool { get; set; } = false;

    /// <summary>
    /// Gets or sets whether completed trace runs are exposed as MCP resources at the
    /// <c>trace://</c> URI scheme, allowing MCP clients to browse execution artifacts.
    /// </summary>
    /// <value>Default: <c>true</c>.</value>
    public bool EnableMcpTraceResources { get; set; } = true;
}
