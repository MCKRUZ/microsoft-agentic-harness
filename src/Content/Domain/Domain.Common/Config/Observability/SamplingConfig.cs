namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for tail-based sampling. Unlike head-based sampling (decide at trace start),
/// tail-based sampling buffers spans and evaluates the complete trace before deciding
/// whether to keep or drop it.
/// </summary>
/// <remarks>
/// <para>
/// Policy evaluation order (first match wins):
/// <list type="number">
///   <item><description>Error traces — always kept when <see cref="AlwaysKeepErrors"/> is true</description></item>
///   <item><description>Slow traces — kept when duration exceeds <see cref="SlowRequestThresholdMs"/></description></item>
///   <item><description>Agent executions — kept when <see cref="AlwaysKeepAgentExecutions"/> is true</description></item>
///   <item><description>Probabilistic — sampled at <see cref="DefaultSamplingPercentage"/> rate</description></item>
/// </list>
/// </para>
/// </remarks>
public class SamplingConfig
{
    /// <summary>
    /// Gets or sets whether tail-based sampling is enabled.
    /// When disabled, all spans pass through unfiltered.
    /// </summary>
    /// <value>Default: true.</value>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how long to wait for a trace to complete before
    /// making the sampling decision. Longer waits produce better decisions
    /// but consume more memory.
    /// </summary>
    /// <value>Default: 30 seconds.</value>
    public TimeSpan DecisionWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum number of traces to buffer concurrently.
    /// When exceeded, oldest traces are force-evaluated.
    /// </summary>
    /// <value>Default: 10,000 traces.</value>
    public int MaxBufferedTraces { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the probabilistic sampling percentage for traces that
    /// don't match any priority policy (errors, slow, agent).
    /// </summary>
    /// <value>Default: 10.0 (keep 10% of normal traces).</value>
    public double DefaultSamplingPercentage { get; set; } = 10.0;

    /// <summary>
    /// Gets or sets whether all error traces are kept regardless of sampling rate.
    /// </summary>
    /// <value>Default: true.</value>
    public bool AlwaysKeepErrors { get; set; } = true;

    /// <summary>
    /// Gets or sets the threshold in milliseconds beyond which a trace is
    /// considered slow and always kept.
    /// </summary>
    /// <value>Default: 5000ms (5 seconds).</value>
    public int SlowRequestThresholdMs { get; set; } = 5_000;

    /// <summary>
    /// Gets or sets whether agent execution traces (those with <c>agent.phase</c>
    /// or <c>gen_ai.system</c> attributes) are always kept.
    /// </summary>
    /// <value>Default: true.</value>
    public bool AlwaysKeepAgentExecutions { get; set; } = true;
}
