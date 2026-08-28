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
/// drop everything." Building this record only through <see cref="FromCurrentSettings"/> means that
/// trap can only be hit by a mock of <see cref="IToolCallReplayTreatment"/> itself — the same warning
/// that interface already carries — never silently through this type.
/// </param>
public sealed record ToolCallReplayWindowPolicy(bool ReplayToolCalls, int MaxReplayedChars)
{
    /// <summary>
    /// Reads <paramref name="treatment"/>'s current property values into a policy. Calling this fresh
    /// on every projection (see <c>ConversationMessageMapping.ToChatMessagesFromLiveSettings</c>)
    /// states "live" — a hot-reloaded config change applies to the very next turn. Calling it once and
    /// reusing the result (see <c>DurableTranscript</c>, which does this in its constructor) states
    /// "snapshot" — fixed for the caller's lifetime, deliberately, because the caller gates one run's
    /// dispatch and a mid-run reload applying half the policy would be worse than applying none of it.
    /// </summary>
    public static ToolCallReplayWindowPolicy FromCurrentSettings(IToolCallReplayTreatment treatment)
    {
        ArgumentNullException.ThrowIfNull(treatment);
        return new ToolCallReplayWindowPolicy(treatment.Enabled, treatment.MaxReplayedChars);
    }
}
