namespace Application.Common.Interfaces.Common;

/// <summary>
/// Resolves well-known directory paths for the agentic harness.
/// Abstracts file system layout so that Infrastructure implementations can
/// vary paths per environment (development vs. container vs. Azure).
/// </summary>
/// <remarks>
/// Registered as singleton in DI. Implementations must ensure all returned
/// paths are absolute and that directories exist (create on first access).
/// </remarks>
public interface IDirectoryMapper
{
    /// <summary>
    /// Gets the absolute path for a well-known harness directory.
    /// </summary>
    /// <param name="directory">The directory type to resolve.</param>
    /// <returns>Absolute path guaranteed to exist.</returns>
    string GetAbsolutePath(HarnessDirectory directory);
}
