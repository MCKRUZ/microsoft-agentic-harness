using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Thrown when a plugin skill manifest (SKILL.md) or agent manifest (AGENT.md) is refused at load
/// because its security scan found a finding at or above the configured block threshold.
/// </summary>
/// <remarks>
/// Named for both call sites deliberately — thrown from both <c>SkillMetadataParser.Build</c> and
/// <c>AgentMetadataParser.ParseFromFile</c>, so a name scoped to "skill" would be wrong at the second
/// site. Modeled on <see cref="SkillParsingException"/>'s shape (a load-time content refusal), not
/// <c>SkillPathRefusedException</c>'s (an access-control refusal with its own HTTP-status contract) —
/// this is a different failure class.
/// <para>
/// Carries the threat type/severity pairs that tripped the refusal as display strings, never the raw
/// scanned text: the scanned content is attacker-supplied by definition, and copying it into an
/// exception message that typically ends up in a log would move the payload into whatever reads the
/// logs next — the same reasoning <c>ScanningMcpToolProvider</c> already applies to tool-description
/// findings.
/// </para>
/// </remarks>
public sealed class ManifestRefusedException : ApplicationExceptionBase
{
    /// <summary>The path to the manifest file that was refused.</summary>
    public string FilePath { get; }

    /// <summary>
    /// The threat type/severity pairs the scan found (e.g. <c>"ToolPoisoning/High"</c>), never the
    /// text that triggered them.
    /// </summary>
    public IReadOnlyList<string> Findings { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestRefusedException"/> class.
    /// </summary>
    /// <param name="filePath">The path to the manifest file that was refused.</param>
    /// <param name="findings">The threat type/severity pairs the scan found.</param>
    public ManifestRefusedException(string filePath, IReadOnlyList<string> findings)
        : base(BuildMessage(filePath, findings))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(findings);

        FilePath = filePath;
        Findings = findings;
    }

    // Tolerates a null findings list so the base constructor call (which necessarily runs before this
    // constructor's own ArgumentNullException.ThrowIfNull below) can't throw the wrong exception type.
    private static string BuildMessage(string filePath, IReadOnlyList<string>? findings) =>
        $"Manifest at '{filePath}' refused: security scan found {string.Join(", ", findings ?? [])}.";
}
