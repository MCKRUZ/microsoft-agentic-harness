namespace Domain.Common.Config.AI.Permissions;

/// <summary>
/// Configuration for the permission system controlling tool and file access approvals.
/// Bound from <c>AppConfig:AI:Permissions</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// The permission system evaluates each tool invocation against configured rules.
/// When no rule matches, <see cref="DefaultBehavior"/> determines the action.
/// Repeated denials for the same pattern trigger automatic denial via
/// <see cref="DenialRateLimitThreshold"/>.
/// </para>
/// </remarks>
public class PermissionsConfig
{
    /// <summary>
    /// Gets or sets the default behavior when no permission rule matches a tool invocation.
    /// Valid values: "Allow", "Ask", "Deny".
    /// </summary>
    public string DefaultBehavior { get; set; } = "Ask";

    /// <summary>
    /// Gets or sets the number of consecutive denials for the same pattern before
    /// the system automatically denies future matching requests without prompting.
    /// </summary>
    public int DenialRateLimitThreshold { get; set; } = 3;

    /// <summary>
    /// Gets or sets the list of file system paths that always require explicit user approval,
    /// regardless of other permission rules. Matches are prefix-based.
    /// </summary>
    public IReadOnlyList<string> SafetyGatePaths { get; set; } = [".git/", ".claude/", ".ssh/", ".env"];

    /// <summary>
    /// Gets or sets the maximum number of subcommands evaluated during pattern matching.
    /// Acts as a safeguard against ReDoS (Regular Expression Denial of Service) when
    /// evaluating complex permission patterns.
    /// </summary>
    public int MaxSubcommandLimit { get; set; } = 50;
}
