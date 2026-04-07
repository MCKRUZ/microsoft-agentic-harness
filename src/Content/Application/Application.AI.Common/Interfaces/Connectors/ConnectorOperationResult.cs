namespace Application.AI.Common.Interfaces.Connectors;

/// <summary>
/// Immutable result of a connector client operation.
/// Provides a consistent structure across all external system integrations
/// with both structured data and LLM-friendly markdown output.
/// </summary>
/// <remarks>
/// Use the <see cref="Success"/> and <see cref="Failure"/> factory methods
/// to create instances rather than constructing directly.
/// </remarks>
public record ConnectorOperationResult
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Error message if operation failed. Null on success.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Result data from the operation (JSON-serializable).
    /// For AI consumption: formatted as markdown or structured data.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// Markdown-formatted result for AI/human consumption.
    /// Preferred over raw <see cref="Data"/> for LLM processing.
    /// </summary>
    public string? MarkdownResult { get; init; }

    /// <summary>
    /// Additional metadata about the operation.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// HTTP status code if operation involved an HTTP call.
    /// </summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>
    /// When the operation was executed (UTC).
    /// </summary>
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="data">Structured result data.</param>
    /// <param name="markdown">Markdown-formatted result for LLM consumption.</param>
    public static ConnectorOperationResult Success(object? data = null, string? markdown = null)
    {
        return new ConnectorOperationResult
        {
            IsSuccess = true,
            Data = data,
            MarkdownResult = markdown
        };
    }

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="errorMessage">Description of the failure.</param>
    /// <param name="httpStatusCode">HTTP status code if applicable.</param>
    public static ConnectorOperationResult Failure(string errorMessage, int? httpStatusCode = null)
    {
        return new ConnectorOperationResult
        {
            IsSuccess = false,
            ErrorMessage = errorMessage,
            HttpStatusCode = httpStatusCode
        };
    }
}
