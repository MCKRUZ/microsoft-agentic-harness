namespace Application.AI.Common.CQRS.Bundles.RegisterBundle;

/// <summary>
/// The result of registering a bundle: the handle the caller uses to run or delete the staged bundle, and
/// when that handle expires if left idle. The handle's lifetime is sliding, so <see cref="ExpiresAt"/> is
/// the earliest it could expire — using the handle refreshes it.
/// </summary>
public sealed record RegisterBundleResult
{
    /// <summary>The opaque handle identifying the staged bundle.</summary>
    public required string Handle { get; init; }

    /// <summary>The earliest instant the handle expires if it is not used again before then.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
