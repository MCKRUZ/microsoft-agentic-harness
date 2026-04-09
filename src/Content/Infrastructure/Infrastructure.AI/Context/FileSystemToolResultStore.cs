using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Context;
using Domain.AI.Context;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Context;

/// <summary>
/// Persists large tool results to disk and serves truncated previews for in-context use.
/// Small results (below <see cref="Domain.Common.Config.AI.ContextManagement.ToolResultStorageConfig.PerResultCharLimit"/>)
/// are returned inline without any disk I/O.
/// </summary>
public sealed class FileSystemToolResultStore : IToolResultStore
{
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<FileSystemToolResultStore> _logger;
    private readonly ConcurrentDictionary<string, string> _resultPaths = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemToolResultStore"/> class.
    /// </summary>
    /// <param name="options">Application configuration for storage thresholds and paths.</param>
    /// <param name="logger">Logger for storage diagnostics.</param>
    public FileSystemToolResultStore(
        IOptionsMonitor<AppConfig> options,
        ILogger<FileSystemToolResultStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ToolResultReference> StoreIfLargeAsync(
        string sessionId,
        string toolName,
        string? operation,
        string fullOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(fullOutput);

        // H-1: Prevent path traversal via sessionId
        if (sessionId != Path.GetFileName(sessionId))
            throw new ArgumentException("Session ID must not contain path separators.", nameof(sessionId));

        var config = _options.CurrentValue.AI.ContextManagement.ToolResultStorage;
        var resultId = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;

        if (fullOutput.Length <= config.PerResultCharLimit)
        {
            _logger.LogDebug(
                "Tool result {ResultId} from {ToolName} is {Length} chars — keeping inline",
                resultId, toolName, fullOutput.Length);

            return new ToolResultReference
            {
                ResultId = resultId,
                ToolName = toolName,
                Operation = operation,
                PreviewContent = fullOutput,
                FullContentPath = null,
                SizeChars = fullOutput.Length,
                Timestamp = timestamp
            };
        }

        var storagePath = Path.Combine(config.StoragePath, sessionId, "tool-results", $"{resultId}.json");
        var directory = Path.GetDirectoryName(storagePath)!;
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(storagePath, fullOutput, cancellationToken);
        _resultPaths[resultId] = storagePath;

        var previewLength = Math.Min(config.PreviewSizeChars, fullOutput.Length);
        var preview = $"{fullOutput[..previewLength]}\n... [{fullOutput.Length} chars persisted to {resultId}]";

        _logger.LogInformation(
            "Tool result {ResultId} from {ToolName} persisted to disk: {Length} chars at {Path}",
            resultId, toolName, fullOutput.Length, storagePath);

        return new ToolResultReference
        {
            ResultId = resultId,
            ToolName = toolName,
            Operation = operation,
            PreviewContent = preview,
            FullContentPath = storagePath,
            SizeChars = fullOutput.Length,
            Timestamp = timestamp
        };
    }

    /// <inheritdoc />
    public async Task<string> RetrieveFullContentAsync(
        string resultId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);

        if (!_resultPaths.TryGetValue(resultId, out var filePath))
        {
            throw new KeyNotFoundException($"No stored result found for id '{resultId}'.");
        }

        _logger.LogDebug("Retrieving full content for result {ResultId} from {Path}", resultId, filePath);

        return await File.ReadAllTextAsync(filePath, cancellationToken);
    }
}
