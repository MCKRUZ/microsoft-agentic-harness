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

    public string NoResultPlaceholder => "[no result recorded]";

    public string Treat(string rawText, string? toolName) => rawText;
}
