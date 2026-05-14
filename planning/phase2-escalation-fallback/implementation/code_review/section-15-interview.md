# Section 15 Code Review Interview

## Interview Decisions
1. **HIGH-1 (Eviction race)**: User chose **Add lock**. Applied `lock (_enqueueLock)` around enqueue+eviction block.
2. **MEDIUM-4 (No interface)**: User chose **Extract ILlmRetryQueue**. Created `Application.AI.Common/Interfaces/Resilience/ILlmRetryQueue.cs` with `EnqueueAsync` method.

## Auto-Fixes Applied
3. **MEDIUM-6 (Dispose ordering)**: Swapped `base.Dispose()` before `_drainSignal.Dispose()` to avoid ObjectDisposedException race.
4. **MEDIUM-7 (Caller cancellation during retry)**: Added `OperationCanceledException` catch clauses in DrainAsync to properly set TCS as cancelled (not faulted) when caller token fires, and re-enqueue when host is shutting down.
5. **HIGH-2 (Cached client)**: Added comment documenting why caching is safe (IResilientChatClientProvider contract).

## Let Go
- MEDIUM-1 (FIFO reorder during sweep): ConcurrentQueue trade-off, not worth custom data structure
- MEDIUM-2 (No CancellationTokenRegistration): Acceptable with 10s sweep interval
- MEDIUM-3 (Depth drift): Transient, no correctness impact
- MEDIUM-5 (Hardcoded 10s interval): Scope expansion beyond section spec
- MEDIUM-8/9 (Lifecycle and OCE tests): Integration tests deferred to section-21
- All LOWs: Inherent constraints or minor style
