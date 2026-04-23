using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Ingestion;

/// <summary>
/// Default document parser that handles Markdown and plain-text files.
/// Reads content from <c>file://</c> URIs via <see cref="File.ReadAllTextAsync(string, CancellationToken)"/>.
/// Validates paths against <c>AppConfig.Infrastructure.FileSystem.AllowedBasePaths</c>
/// to prevent path traversal. Other URI schemes throw <see cref="NotSupportedException"/>.
/// </summary>
public sealed class MarkdownDocumentParser : IDocumentParser
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.Ingestion");
    private static readonly IReadOnlyList<string> Extensions = [".md", ".markdown", ".txt"];

    private readonly IOptionsMonitor<AppConfig> _appConfig;

    public MarkdownDocumentParser(IOptionsMonitor<AppConfig> appConfig)
    {
        _appConfig = appConfig;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => Extensions;

    /// <inheritdoc />
    public async Task<string> ParseAsync(Uri documentUri, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.ingest.parse");
        activity?.SetTag(RagConventions.ModelOperation, "document_parse");
        activity?.SetTag("rag.ingest.uri", documentUri.ToString());

        if (!documentUri.IsFile)
        {
            throw new NotSupportedException(
                $"URI scheme '{documentUri.Scheme}' is not supported by {nameof(MarkdownDocumentParser)}. " +
                "Only file:// URIs are supported.");
        }

        var localPath = Path.GetFullPath(documentUri.LocalPath);
        var extension = Path.GetExtension(localPath);

        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Extension '{extension}' is not supported. Supported: {string.Join(", ", SupportedExtensions)}");
        }

        ValidatePathIsAllowed(localPath);

        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException($"Document not found at '{localPath}'.", localPath);
        }

        var content = await File.ReadAllTextAsync(localPath, cancellationToken);

        activity?.SetTag("rag.ingest.content_length", content.Length);
        return content;
    }

    private void ValidatePathIsAllowed(string fullPath)
    {
        var allowedPaths = _appConfig.CurrentValue.Infrastructure?.FileSystem?.AllowedBasePaths;
        if (allowedPaths is null or { Count: 0 })
            throw new UnauthorizedAccessException("No AllowedBasePaths configured for RAG file ingestion.");

        var normalizedTarget = Path.GetFullPath(fullPath);
        foreach (var basePath in allowedPaths)
        {
            var normalizedBase = Path.GetFullPath(basePath);
            if (normalizedTarget.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                return;
        }

        throw new UnauthorizedAccessException(
            $"Path '{fullPath}' is outside all configured AllowedBasePaths.");
    }
}
