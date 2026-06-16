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

        // H-1: sessionId must be a single safe path segment. Path.GetFileName equality
        // alone is insufficient — it lets "." and ".." through (GetFileName(("..")) == "..")
        // which Path.Combine then resolves to a parent directory, escaping the storage root.
        var safeSessionId = SanitizeSessionSegment(sessionId);

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

        var storagePath = Path.Combine(config.StoragePath, safeSessionId, "tool-results", $"{resultId}.json");
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

    /// <summary>
    /// Reduces <paramref name="sessionId"/> to a single safe path segment for use in
    /// <see cref="Path.Combine(string, string)"/>, rejecting any value that could escape
    /// the storage root via path traversal.
    /// </summary>
    /// <param name="sessionId">The caller-supplied session identifier.</param>
    /// <returns>The validated session identifier, guaranteed to be a single path segment.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sessionId"/> contains path separators, is rooted, or is a
    /// relative directory reference ("." or "..").
    /// </exception>
    private static string SanitizeSessionSegment(string sessionId)
    {
        // Reject any path separator from EITHER platform, a rooted path, or a relative
        // directory reference. Path.GetFileName / Path.IsPathRooted are OS-specific — on
        // Linux '\' is an ordinary character, so a Windows-style "..\escape" slips through
        // GetFileName unchanged. Check both separators explicitly so the guard behaves
        // identically on every platform.
        if (sessionId.Contains('/') || sessionId.Contains('\\') || Path.IsPathRooted(sessionId))
        {
            throw new ArgumentException(
                "Session ID must be a single path segment without separators.", nameof(sessionId));
        }

        // "." and ".." resolve to the storage root or a parent when combined, so reject them.
        if (sessionId is "." or "..")
        {
            throw new ArgumentException(
                "Session ID must not be a relative directory reference.", nameof(sessionId));
        }

        return sessionId;
    }
}
