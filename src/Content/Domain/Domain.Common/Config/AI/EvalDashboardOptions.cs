namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the eval dashboard persistence subsystem (Sub-phase 5.4.1).
/// </summary>
/// <remarks>
/// <para>
/// Disabled by default. When enabled, ingested <c>EvalRunReport</c>s are persisted to
/// a SQLite database so the dashboard can browse run history, drill into per-case scores,
/// and aggregate metric trends across prompt versions. Matches the
/// <see cref="PromptUsageOptions"/> opt-in shape so consumers can wire both stores
/// against the same SQLite file or against separate files.
/// </para>
/// <para>
/// Bind via <c>services.Configure&lt;EvalDashboardOptions&gt;(config.GetSection("AI:EvalDashboard"))</c>
/// or set programmatically through the <c>AddEvalDashboardPersistence</c> extension.
/// </para>
/// </remarks>
public sealed class EvalDashboardOptions
{
    /// <summary>Configuration section name when bound via IConfiguration.</summary>
    public const string SectionName = "AI:EvalDashboard";

    /// <summary>
    /// When <c>true</c>, the eval dashboard SQLite store and its CQRS read/write
    /// surface are registered. Defaults to <c>false</c>: the template ethos is
    /// zero-infra by default — consumers opt in when they want a dashboard.
    /// </summary>
    public bool PersistenceEnabled { get; init; }

    /// <summary>
    /// EF Core connection string for the eval dashboard SQLite database.
    /// Defaults to <c>Data Source=eval-dashboard.db</c> in the process working directory.
    /// </summary>
    public string ConnectionString { get; init; } = "Data Source=eval-dashboard.db";
}
