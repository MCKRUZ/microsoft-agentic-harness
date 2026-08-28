using Application.AI.Common.Interfaces;

namespace Application.AI.Common.Models.Conversations;

/// <summary>
/// The two <see cref="IToolCallReplayTreatment"/> settings that together govern one window
/// projection — bundled so a caller states "read live, every projection" or "snapshot once, fix for
/// the run" as an explicit choice of when it calls <see cref="FromCurrentSettings"/>, rather than as
/// an accidental convention two call sites happened to follow differently.
/// </summary>
/// <param name="ReplayToolCalls">
/// See <see cref="ConversationMessageMapping.ToChatMessages(IReadOnlyList{ConversationMessage},bool,int,Microsoft.Extensions.Logging.ILogger?)"/>'s
/// <c>replayToolCalls</c> parameter.
/// </param>
/// <param name="MaxReplayedChars">
/// See <see cref="ConversationMessageMapping.ToChatMessages(IReadOnlyList{ConversationMessage},bool,int,Microsoft.Extensions.Logging.ILogger?)"/>'s
/// <c>maxReplayedChars</c> parameter. This record has no parameterless constructor and no default for
/// either field, on purpose: <see cref="IToolCallReplayTreatment.MaxReplayedChars"/>'s own remarks
/// warn that an unstubbed mock returns <see langword="default"/>, silently reading as "zero budget,
/// drop everything." Every <em>production</em> path builds this record through
/// <see cref="FromCurrentSettings"/> — the constructor stays public, as an ordinary record's does, so
/// a test can still construct one directly — which means the trap can only be hit by a mock of
/// <see cref="IToolCallReplayTreatment"/> itself, the same warning that interface already carries,
/// never silently through this type on the production path.
/// </param>
public sealed record ToolCallReplayWindowPolicy(bool ReplayToolCalls, int MaxReplayedChars)
{
    /// <summary>
    /// Reads <paramref name="treatment"/>'s current property values into a policy. Calling this fresh
    /// on every projection — <c>AgUiRunHandler.ToMeaiHistory</c> and
    /// <c>ConversationOrchestrator.ToMeaiHistory</c> both call
    /// <c>ConversationMessageMapping.ToChatMessages(messages, FromCurrentSettings(treatment), logger)</c>
    /// once per turn — states "live": a hot-reloaded config change applies starting the very next turn.
    /// Previously each of those two files hand-wrote the <see cref="IToolCallReplayTreatment.Enabled"/>/
    /// <see cref="IToolCallReplayTreatment.MaxReplayedChars"/> unpacking separately; a role or setting
    /// added to one copy and not the other was a silent split, not a compile error. Calling this once
    /// and reusing the result — <c>DurableTranscript</c>, in its constructor — states "snapshot": fixed
    /// for the caller's lifetime, deliberately, because that caller gates one run's dispatch and a
    /// mid-run reload applying half the policy would be worse than applying none of it. Same factory,
    /// called at a different point in the caller's lifetime — that is what makes "live vs. snapshot" an
    /// explicit choice each caller states, rather than an accident of which file it happens to be.
    /// </summary>
    public static ToolCallReplayWindowPolicy FromCurrentSettings(IToolCallReplayTreatment treatment)
    {
        ArgumentNullException.ThrowIfNull(treatment);
        return new ToolCallReplayWindowPolicy(treatment.Enabled, treatment.MaxReplayedChars);
    }
}
