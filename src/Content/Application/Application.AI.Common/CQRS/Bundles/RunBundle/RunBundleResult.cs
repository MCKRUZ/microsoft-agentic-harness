namespace Application.AI.Common.CQRS.Bundles.RunBundle;

/// <summary>
/// The result of starting a bundle run: the job id the caller polls (via <c>GetBundleRunQuery</c>) for the
/// run's evolving status and eventual outcome.
/// </summary>
public sealed record RunBundleResult
{
    /// <summary>The opaque id of the queued run job.</summary>
    public required string JobId { get; init; }
}
