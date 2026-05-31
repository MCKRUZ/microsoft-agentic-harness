namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the prompt-usage persistence subsystem.
/// </summary>
/// <remarks>
/// <para>
/// Disabled by default. When enabled, the registry's recorder pipeline gains a
/// SQLite-backed durable store so trace-replay can reconstruct prompt assignments
/// across process restarts. OTel tagging (the always-on path) remains in place
/// either way.
/// </para>
/// <para>
/// Bind via <c>services.Configure&lt;PromptUsageOptions&gt;(config.GetSection("AI:PromptUsage"))</c>
/// or set programmatically through the <c>AddPromptRegistry</c> overload.
/// </para>
/// </remarks>
public sealed class PromptUsageOptions
{
    /// <summary>Configuration section name when bound via IConfiguration.</summary>
    public const string SectionName = "AI:PromptUsage";

    /// <summary>
    /// When <c>true</c>, prompt usage rows are persisted to SQLite alongside the
    /// always-on OTel tagging. Defaults to <c>false</c>: the template ethos is
    /// zero-infra by default — consumers opt in when they need durable usage history.
    /// </summary>
    public bool PersistenceEnabled { get; init; }

    /// <summary>
    /// EF Core connection string for the prompt-usage SQLite database.
    /// Defaults to <c>Data Source=prompt-usage.db</c> in the process working directory.
    /// </summary>
    public string ConnectionString { get; init; } = "Data Source=prompt-usage.db";
}
