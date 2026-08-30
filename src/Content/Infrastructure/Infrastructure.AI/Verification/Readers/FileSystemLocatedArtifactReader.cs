using Application.AI.Common.Interfaces.ClaimVerification;
using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Verification.Readers;

/// <summary>
/// The <c>"file"</c>-scheme <see cref="ILocatedArtifactReader"/>: resolves a
/// <c>"file:relative/path.cs"</c> or <c>"file:relative/path.cs:42"</c> location by reading the path
/// through the same sandbox every other file read in this repo goes through.
/// </summary>
/// <remarks>
/// Deliberately depends on <see cref="IFileSystemService"/> — the same sandboxed, size-limited,
/// symlink-canonicalizing guard <c>FileSystemTool</c> wraps for model-initiated reads — rather than
/// calling <c>File.ReadAllTextAsync</c> directly. A claim's <c>Location</c> is model-supplied text;
/// treating it as a trusted path would reopen exactly the traversal surface
/// <c>SandboxedPathGuard</c> exists to close.
/// </remarks>
public sealed class FileSystemLocatedArtifactReader : ILocatedArtifactReader
{
    private const string SchemePrefix = "file:";

    private readonly IFileSystemService _fileSystem;
    private readonly ILogger<FileSystemLocatedArtifactReader> _logger;

    /// <summary>Initializes a new instance of the <see cref="FileSystemLocatedArtifactReader"/> class.</summary>
    public FileSystemLocatedArtifactReader(IFileSystemService fileSystem, ILogger<FileSystemLocatedArtifactReader> logger)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logger);
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> TryReadAsync(string location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);

        var (path, line) = ExtractPathAndLine(location);
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("Malformed file location '{Location}' — no path after the 'file:' prefix.", location);
            return null;
        }

        string content;
        try
        {
            content = await _fileSystem.ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
        {
            // A claim citing a path outside the sandbox — traversal, a protected directory, a bad
            // pattern — is refused, not read. Reported identically to "not found" so a claim's
            // confidence never carries a distinction between "doesn't exist" and "exists but
            // forbidden," which would leak sandbox layout through verification output.
            _logger.LogWarning(ex, "Refused to read '{Path}' cited by a claim — the sandbox rejected the path.", path);
            return null;
        }

        return line is null ? content : $"(the claim cites line {line})\n{content}";
    }

    private static (string? Path, int? Line) ExtractPathAndLine(string location)
    {
        if (!location.StartsWith(SchemePrefix, StringComparison.Ordinal))
        {
            return (null, null);
        }

        var remainder = location[SchemePrefix.Length..];
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return (null, null);
        }

        var lastColon = remainder.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(remainder[(lastColon + 1)..], out var line))
        {
            return (remainder[..lastColon], line);
        }

        return (remainder, null);
    }
}
