namespace Domain.AI.Verification;

/// <summary>
/// One specific reliance extracted from an artifact: for the code at <see cref="Where"/> to be
/// correct, <see cref="Property"/> must hold true at <see cref="ReliesOn"/>. Asking a model to name
/// obligations rather than "find problems" is what makes an obligation dispatchable — a verifier
/// goes and reads <see cref="ReliesOn"/> directly, rather than judging plausibility from the same
/// context the extraction saw.
/// </summary>
/// <param name="Where">The location creating the reliance (e.g. a file path and line range).</param>
/// <param name="ReliesOn">
/// The other location the code at <see cref="Where"/> depends on being true — specific enough for a
/// verifier to locate and read (e.g. a file path and line range, a config key, a resource id).
/// </param>
/// <param name="Property">What must hold at <see cref="ReliesOn"/> for <see cref="Where"/> to be correct.</param>
public sealed record Obligation(string Where, string ReliesOn, string Property);
