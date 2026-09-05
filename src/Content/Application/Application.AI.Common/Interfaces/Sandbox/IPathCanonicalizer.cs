namespace Application.AI.Common.Interfaces.Sandbox;

/// <summary>
/// Resolves an already-normalized filesystem path to its canonical, link-resolved absolute form, so
/// a containment comparison (<see cref="ICapabilityEnforcer"/>'s path scoping) cannot be defeated by a
/// symlink or junction that spells a different path to the same — or a different — location than it
/// appears to name.
/// </summary>
/// <remarks>
/// Implemented in Infrastructure, where filesystem I/O belongs, and consumed here as an interface so
/// <c>CapabilityEnforcer</c> (Application layer) never references the filesystem directly. Optional at
/// every call site that takes one: a host that doesn't register an implementation still gets the
/// normalized-string comparison <c>CapabilityEnforcer</c> falls back to, just without symlink
/// resolution — a real but bounded loss of guarantee, not a broken build.
/// </remarks>
public interface IPathCanonicalizer
{
    /// <summary>
    /// Returns the canonical absolute form of <paramref name="normalizedPath"/> — the same identity a
    /// file access at that path would actually resolve to, links included — or the input unchanged
    /// when the entry cannot be inspected (missing, unreadable, or an unsupported path shape), which is
    /// the safe answer: a path that doesn't exist yet holds nothing to alias.
    /// </summary>
    /// <param name="normalizedPath">An already path-normalized (absolute, separator-trimmed) input.</param>
    string Canonicalize(string normalizedPath);
}
