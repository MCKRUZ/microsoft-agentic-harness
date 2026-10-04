namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Where an armed child execution context gets its call-once scope id — the key a
/// "call once per conversation/run" tool is enforced against.
/// </summary>
public enum CallOnceScopeSource
{
    /// <summary>
    /// No scope. A call-once gate fails open on a null scope, which is the documented answer for a
    /// surface with no request-level session to key a repeat-call check on (a direct tool invocation).
    /// </summary>
    Omit,

    /// <summary>
    /// The parent's scope, passed through unchanged — a null parent scope stays null. A call-once tool
    /// the parent already claimed stays claimed in the child.
    /// </summary>
    InheritAsIs,

    /// <summary>
    /// The parent's scope, and when the parent has none (null or empty, or no parent at all) the
    /// caller-supplied fallback id, so the child is still enforced — narrower than the true scope is
    /// merely inconvenient, never a leak.
    /// </summary>
    InheritOrFallback,
}
