using Application.AI.Common.Interfaces;

namespace Application.Core.Tests.Fakes;

/// <summary>
/// An <see cref="IToolCallReplayTreatment"/> that returns text unchanged, for
/// <c>ExecuteAgentTurnCommandHandler</c> tests that construct the handler directly and don't
/// exercise tool-call treatment behaviour — the real <c>ToolCallReplayTreatment</c> needs a sanitizer,
/// a redaction filter and app config wired up, none of which these tests are about.
/// </summary>
internal sealed class PassthroughToolCallReplayTreatment : IToolCallReplayTreatment
{
    public bool Enabled => true;

    /// <summary>
    /// Settable so a test about the per-turn cap can tighten it. Defaults to the same 32 the real
    /// config does, so every test that is not about the cap sees production behaviour.
    /// </summary>
    public int MaxCallsPerTurn { get; set; } = 32;

    /// <summary>
    /// Settable for the same reason as <see cref="MaxCallsPerTurn"/>, defaulting to the config's own
    /// 65536.
    /// </summary>
    public int MaxReplayedChars { get; set; } = 65536;

    public string NoResultPlaceholder => "[no result recorded]";

    public string Treat(string rawText, string? toolName) => rawText;
}
