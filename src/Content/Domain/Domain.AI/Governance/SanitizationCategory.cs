namespace Domain.AI.Governance;

/// <summary>
/// Classifies the type of content detected during response sanitization.
/// </summary>
public enum SanitizationCategory
{
    /// <summary>No issue detected.</summary>
    None,

    /// <summary>Leaked credential or secret (API key, token, connection string).</summary>
    CredentialLeak,

    /// <summary>Prompt injection pattern in tool output.</summary>
    PromptInjection,

    /// <summary>Data exfiltration URL targeting external services.</summary>
    ExfiltrationUrl
}
