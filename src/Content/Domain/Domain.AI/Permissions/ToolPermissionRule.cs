namespace Domain.AI.Permissions;

/// <summary>
/// A single permission rule that matches tool invocations by name and optional operation pattern.
/// Rules are evaluated in priority order during the 3-phase resolution algorithm.
/// </summary>
/// <param name="ToolPattern">Glob pattern matching tool names (e.g., "file_system", "bash:*", "*").</param>
/// <param name="OperationPattern">Optional glob pattern matching operations (e.g., "read", "write:*"). Null matches all operations.</param>
/// <param name="Behavior">The permission behavior when this rule matches.</param>
/// <param name="Source">Where this rule originated.</param>
/// <param name="Priority">Evaluation priority. Lower values are checked first.</param>
/// <param name="IsBypassImmune">When true, this rule cannot be overridden by bypass/auto-approve modes.</param>
/// <param name="IsAuthoritativeBaseline">
/// When true, this rule is an operator-set authoritative baseline for the tools it matches: it takes
/// precedence over generic tier/default rules in <em>both</em> directions (a specific Allow beats a
/// generic Ask, and a specific Ask beats a generic Allow), independent of the resolver's normal
/// Deny&gt;Ask&gt;Allow phase ordering. It is evaluated after safety gates and ordinary Deny rules, so a
/// Deny (including a bypass-immune <c>DeniedTools</c> rule) or a safety gate still wins over it. This is
/// how a plugin's <c>AutonomyLevel</c> scopes the autonomy of its own tools without editing the
/// global default tier. Ordinary rules leave this false and are unaffected.
/// <para>
/// Baselines are resolved <em>only</em> in the baseline phase — never by the phase-ordered Deny/Ask/Allow
/// scans — and are arbitrated there by pattern specificity first, then restrictiveness. A provider can
/// therefore close its own allowlist by pairing per-name baselines with a catch-all
/// <c>"*"</c> baseline Deny: the named rules are more specific and win, and anything they do not cover
/// falls through to the Deny. Use a plain (non-baseline) Deny when the intent is an unconditional
/// prohibition rather than a fallback.
/// </para>
/// <para>
/// Specificity arbitration only holds <em>within</em> a <see cref="BaselineTier"/>. A provider closing an
/// allowlist that other providers must not be able to widen has to say so with
/// <see cref="PermissionBaselineTier.GrantBoundary"/>; otherwise a peer provider's exact-name baseline
/// outranks the catch-all and reopens it.
/// </para>
/// </param>
/// <param name="BaselineTier">
/// How authoritative this rule is relative to the baselines of <em>other</em> providers. Meaningful only
/// when <paramref name="IsAuthoritativeBaseline"/> is true. Defaults to
/// <see cref="PermissionBaselineTier.Default"/>: an ordinary baseline that peers may outrank on
/// specificity. <see cref="PermissionBaselineTier.GrantBoundary"/> marks a rule as the edge of an
/// authorisation grant, which no Default-tier baseline may widen past.
/// </param>
public sealed record ToolPermissionRule(
    string ToolPattern,
    string? OperationPattern,
    PermissionBehaviorType Behavior,
    PermissionRuleSource Source,
    int Priority,
    bool IsBypassImmune = false,
    bool IsAuthoritativeBaseline = false,
    PermissionBaselineTier BaselineTier = PermissionBaselineTier.Default);
