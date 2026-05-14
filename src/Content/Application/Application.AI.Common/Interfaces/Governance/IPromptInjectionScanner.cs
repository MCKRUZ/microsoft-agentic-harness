using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Deterministic prompt injection detection using pattern matching.
/// Complements the LLM-based <c>ITextContentSafetyService</c> with
/// zero-latency, zero-cost pattern-based detection.
/// </summary>
public interface IPromptInjectionScanner
{
    /// <summary>
    /// Scans input text for prompt injection patterns.
    /// </summary>
    /// <param name="input">The text to scan (user message, tool output, etc.).</param>
    /// <returns>Scan result with threat classification and matched patterns.</returns>
    InjectionScanResult Scan(string input);
}
