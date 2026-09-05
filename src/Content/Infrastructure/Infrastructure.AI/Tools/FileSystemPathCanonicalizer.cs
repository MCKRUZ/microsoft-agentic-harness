using Application.AI.Common.Interfaces.Sandbox;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Adapts <see cref="SandboxPathCanonicalizer"/> — the filesystem-facing resolver
/// <see cref="SandboxedPathGuard"/> already uses — as an <see cref="IPathCanonicalizer"/> for
/// <c>CapabilityEnforcer</c> (Application layer), which cannot reference the filesystem directly.
/// </summary>
internal sealed class FileSystemPathCanonicalizer : IPathCanonicalizer
{
    /// <inheritdoc />
    public string Canonicalize(string normalizedPath) => SandboxPathCanonicalizer.Canonicalize(normalizedPath);
}
