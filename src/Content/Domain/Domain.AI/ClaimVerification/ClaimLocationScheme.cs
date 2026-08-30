namespace Domain.AI.ClaimVerification;

/// <summary>
/// The scheme names <see cref="Claim.Location"/> is built from and dispatched on — the single
/// source of truth every claim-constructing caller and every keyed
/// <c>ILocatedArtifactReader</c> registration shares, so the two can never drift apart the way two
/// independently-retyped string literals could.
/// </summary>
/// <remarks>
/// Mirrors this repo's own established idiom for a DI-key string a producer and a consumer must
/// agree on byte-for-byte — <c>FileSystemTool.ToolName</c> and <c>WellKnownGateKeys.Policy</c> are
/// both a type-owned constant referenced by symbol at every registration and construction site,
/// never a bare literal retyped per call site. Lives in <c>Domain.AI</c> (not on either
/// <c>ILocatedArtifactReader</c> implementation, which are Infrastructure-layer) specifically so an
/// Application-layer claim-constructing caller — which cannot reference Infrastructure — can still
/// build a correct <see cref="Claim.Location"/> without retyping the scheme name.
/// </remarks>
public static class ClaimLocationScheme
{
    /// <summary>The <c>"file"</c> scheme — a workspace-relative path, optionally with a trailing <c>:line</c>.</summary>
    public const string File = "file";

    /// <summary>The <c>"config"</c> scheme — a dotted path into the live <c>AppConfig</c> snapshot.</summary>
    public const string Config = "config";
}
