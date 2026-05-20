namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for the SQL database retrieval source.
/// Bound to <c>AppConfig:AI:Rag:SqlDatabase</c>.
/// Disabled by default — opt-in only.
/// </summary>
public sealed class SqlDatabaseConfig
{
    /// <summary>Enable/disable the SQL database retrieval source.</summary>
    public bool Enabled { get; set; }

    /// <summary>Path to the JSON file containing SQL query templates.</summary>
    public string TemplatesPath { get; set; } = "sql-templates.json";

    /// <summary>Allow LLM-generated SQL when no template matches. Set false for high-security environments.</summary>
    public bool AllowLlmFallback { get; set; } = true;

    /// <summary>Maximum rows returned per query.</summary>
    public int MaxRows { get; set; } = 100;

    /// <summary>Query execution timeout in seconds.</summary>
    public int QueryTimeoutSeconds { get; set; } = 5;

    /// <summary>Minimum confidence threshold (0.0-1.0) for template matching.</summary>
    public double TemplateMatchConfidenceThreshold { get; set; } = 0.7;

    /// <summary>Database schema description passed to the LLM for text-to-SQL generation.
    /// Include table/column definitions so the LLM can generate valid queries.</summary>
    public string DatabaseSchema { get; set; } = "";
}
