using Application.AI.Common.Interfaces.Connectors;
using Ardalis.GuardClauses;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Infrastructure.AI.Connectors.Core;

/// <summary>
/// Base class for connector clients providing common functionality.
/// Handles availability checks, operation dispatch, parameter validation,
/// HTTP client configuration, logging, and error handling.
/// </summary>
/// <remarks>
/// Derived classes implement:
/// <list type="bullet">
///   <item><description><see cref="ToolName"/> — unique tool identifier</description></item>
///   <item><description><see cref="IsAvailable"/> — configuration check</description></item>
///   <item><description><see cref="SupportedOperations"/> — operation whitelist</description></item>
///   <item><description><see cref="ExecuteOperationAsync"/> — operation dispatch logic</description></item>
/// </list>
/// The base class orchestrates the execution pipeline:
/// availability check → operation validation → parameter validation → dispatch → error handling.
/// </remarks>
public abstract class ConnectorClientBase : IConnectorClient
{
    #region Variables

    /// <summary>Logger instance for derived classes.</summary>
    protected readonly ILogger _logger;

    /// <summary>HTTP client factory for creating named or default HTTP clients.</summary>
    protected readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Application configuration monitor for runtime config access.</summary>
    protected readonly IOptionsMonitor<AppConfig> _appConfig;

    /// <summary>Shared JSON serializer options (case-insensitive, indented).</summary>
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of <see cref="ConnectorClientBase"/>.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="httpClientFactory">HTTP client factory for outbound calls.</param>
    /// <param name="appConfig">Application configuration monitor.</param>
    protected ConnectorClientBase(
        ILogger logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppConfig> appConfig)
    {
        Guard.Against.Null(logger, nameof(logger));
        Guard.Against.Null(httpClientFactory, nameof(httpClientFactory));
        Guard.Against.Null(appConfig, nameof(appConfig));

        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _appConfig = appConfig;
    }

    #endregion

    #region IConnectorClient Implementation

    /// <inheritdoc/>
    public abstract string ToolName { get; }

    /// <inheritdoc/>
    public abstract bool IsAvailable { get; }

    /// <inheritdoc/>
    public abstract IReadOnlyList<string> SupportedOperations { get; }

    /// <inheritdoc/>
    public async Task<ConnectorOperationResult> ExecuteAsync(
        string operation,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken = default)
    {
        Guard.Against.NullOrWhiteSpace(operation, nameof(operation));
        Guard.Against.Null(parameters, nameof(parameters));

        if (!IsAvailable)
        {
            _logger.LogWarning("Connector '{ToolName}' is not available (not configured)", ToolName);
            return ConnectorOperationResult.Failure(
                $"Tool '{ToolName}' is not configured. Check configuration and credentials.");
        }

        if (!SupportedOperations.Contains(operation))
        {
            _logger.LogError(
                "Operation '{Operation}' is not supported by '{ToolName}'. Supported: {SupportedOps}",
                operation, ToolName, string.Join(", ", SupportedOperations));
            return ConnectorOperationResult.Failure(
                $"Operation '{operation}' is not supported. Supported: {string.Join(", ", SupportedOperations)}");
        }

        var validationErrors = await ValidateParametersAsync(operation, parameters);
        if (validationErrors.Count > 0)
        {
            _logger.LogError("Parameter validation failed for '{Operation}': {Errors}",
                operation, string.Join(", ", validationErrors));
            return ConnectorOperationResult.Failure(
                $"Invalid parameters: {string.Join(", ", validationErrors)}");
        }

        _logger.LogInformation("Executing connector operation: {ToolName}.{Operation}", ToolName, operation);

        try
        {
            var result = await ExecuteOperationAsync(operation, parameters, cancellationToken);
            _logger.LogInformation("Connector operation completed: {ToolName}.{Operation}, Success={Success}",
                ToolName, operation, result.IsSuccess);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connector operation failed: {ToolName}.{Operation}", ToolName, operation);
            return ConnectorOperationResult.Failure($"Operation failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public virtual Task<List<string>> ValidateParametersAsync(
        string operation,
        Dictionary<string, object> parameters)
    {
        return Task.FromResult(new List<string>());
    }

    #endregion

    #region Abstract Methods

    /// <summary>
    /// Executes the actual operation. Implemented by derived classes.
    /// Called after availability, operation, and parameter validation pass.
    /// </summary>
    protected abstract Task<ConnectorOperationResult> ExecuteOperationAsync(
        string operation,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken);

    #endregion

    #region Error Handling

    /// <summary>
    /// Wraps an operation with standard HTTP/JSON error handling.
    /// Eliminates repetitive try/catch blocks in derived classes.
    /// </summary>
    /// <param name="operationName">Name for error logging.</param>
    /// <param name="action">The operation to execute.</param>
    protected async Task<ConnectorOperationResult> ExecuteWithErrorHandlingAsync(
        string operationName,
        Func<Task<ConnectorOperationResult>> action)
    {
        try
        {
            return await action();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error during {Operation}", operationName);
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error during {Operation}", operationName);
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    #endregion

    #region HTTP Helper Methods

    /// <summary>
    /// Gets a required parameter from the dictionary.
    /// Throws <see cref="ArgumentException"/> if missing or wrong type.
    /// </summary>
    protected T GetRequiredParameter<T>(Dictionary<string, object> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value))
            throw new ArgumentException($"Required parameter '{key}' is missing");

        if (value is T typedValue)
            return typedValue;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T))!;
        }
        catch
        {
            throw new ArgumentException(
                $"Parameter '{key}' must be of type {typeof(T).Name}, but was {value?.GetType().Name ?? "null"}");
        }
    }

    /// <summary>
    /// Gets an optional parameter from the dictionary.
    /// Returns <paramref name="defaultValue"/> if missing.
    /// </summary>
    protected T? GetOptionalParameter<T>(
        Dictionary<string, object> parameters,
        string key,
        T? defaultValue = default)
    {
        if (!parameters.TryGetValue(key, out var value))
            return defaultValue;

        if (value is T typedValue)
            return typedValue;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T))!;
        }
        catch
        {
            _logger.LogWarning("Failed to convert parameter '{Key}' to {Type}, using default", key, typeof(T).Name);
            return defaultValue;
        }
    }

    /// <summary>
    /// Creates an HTTP client using the named client for this connector's <see cref="ToolName"/>.
    /// </summary>
    protected HttpClient CreateHttpClient()
    {
        return _httpClientFactory.CreateClient(ToolName);
    }

    /// <summary>
    /// Configures HTTP client with common base settings (timeout, clears existing headers).
    /// </summary>
    protected void ConfigureHttpClientBase(HttpClient httpClient, int timeoutSeconds)
    {
        httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        httpClient.DefaultRequestHeaders.Clear();
    }

    /// <summary>Adds Bearer token authentication.</summary>
    protected void AddBearerAuth(HttpClient httpClient, string token)
    {
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Adds Basic authentication.</summary>
    protected void AddBasicAuth(HttpClient httpClient, string username, string password)
    {
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    /// <summary>Adds Accept header.</summary>
    protected void AddAcceptHeader(HttpClient httpClient, string mediaType = "application/json")
    {
        httpClient.DefaultRequestHeaders.Add("Accept", mediaType);
    }

    /// <summary>Adds User-Agent header.</summary>
    protected void AddUserAgentHeader(HttpClient httpClient, string userAgent)
    {
        httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
    }

    /// <summary>Creates JSON string content for HTTP requests.</summary>
    protected StringContent CreateJsonContent<T>(T payload)
    {
        return new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");
    }

    /// <summary>Creates JSON string content from a raw JSON string.</summary>
    protected StringContent CreateJsonContent(string jsonString)
    {
        return new StringContent(jsonString, Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Checks if HTTP response indicates an error and creates a failure result.
    /// Returns null if the response is successful.
    /// </summary>
    protected async Task<ConnectorOperationResult?> CheckHttpErrorAsync(
        HttpResponseMessage response,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return null;

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        return ConnectorOperationResult.Failure(
            $"{operationName} failed: {response.StatusCode} - {error}",
            (int)response.StatusCode);
    }

    #endregion
}
