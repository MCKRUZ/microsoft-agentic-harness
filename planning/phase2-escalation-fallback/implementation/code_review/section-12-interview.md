# Section 12: Code Review Interview

## Triage Summary

| # | Finding | Disposition |
|---|---------|-------------|
| 1 | Mid-stream fallback leaks partial data (HIGH) | Let go — spec explicitly documents as acceptable tradeoff |
| 2 | Streaming bypasses Polly pipeline (HIGH) | Asked user → Wrap stream initiation in pipeline |
| 3 | `lastException!` null-forgiving NRE risk (HIGH) | Auto-fix — conditional throw without null-forgiving |
| 4 | Streaming path loses inner exception (MED) | Auto-fix — track lastException in streaming loop |
| 5 | Dispose is sync-only (MED) | Let go — IChatClient defines IDisposable only |
| 6 | No CancellationToken check at entry (MED) | Auto-fix — added ThrowIfCancellationRequested |
| 7 | GetService doesn't delegate (MED) | Let go — spec says return own metadata only |
| 8 | Test fake async iterator behavior (MED) | Let go — tests pass correctly |
| 9-12 | Low items | Let go |

## Interview

**Q: Streaming path bypasses Polly pipeline entirely. Section 11's BuildForStreamInitiation exists for this. Should we wrap stream initiation in the non-generic pipeline, or leave as try-catch only?**
A: User chose "Wrap initiation" — stream initiation now executes through `provider.StreamPipeline` (non-generic `ResiliencePipeline`). Adds timeout + circuit recording for stream start.

## Applied Fixes

1. Added `cancellationToken.ThrowIfCancellationRequested()` at entry of both `GetResponseAsync` and `GetStreamingResponseAsync`
2. Removed null-forgiving `!` operator — now uses conditional throw: `lastException is not null ? new(..., lastException) : new(...)`
3. Added `lastException` tracking in streaming loop (mid-stream and initiation failures)
4. Wrapped stream initiation in `provider.StreamPipeline.ExecuteAsync(...)` — uses non-generic pipeline from `ProviderResiliencePipelineBuilder.BuildForStreamInitiation`
5. Added `StreamPipeline` parameter to `ProviderEntry` record
6. Added XML doc noting mid-stream partial delivery behavior
7. Updated test helper to pass `ResiliencePipeline.Empty` for stream pipeline

All 9 tests pass after fixes.
