# Section 15 Code Review: LlmRetryQueue

## Summary
- 0 CRITICAL, 2 HIGH, 9 MEDIUM, 5 LOW
- Verdict: WARNING — can merge with targeted fixes

## HIGH
1. **HIGH-1**: Eviction race under concurrent EnqueueAsync — two threads can double-evict. Bounded consequence.
2. **HIGH-2**: `_cachedClient` never invalidated. Safe per IResilientChatClientProvider contract (immutable at runtime).

## MEDIUM
1. **MEDIUM-1**: SweepExpired re-enqueue changes FIFO order. ConcurrentQueue trade-off.
2. **MEDIUM-2**: No CancellationTokenRegistration for proactive removal. Acceptable for simplicity.
3. **MEDIUM-3**: `_queueDepth` can drift transiently from actual count. Not a correctness issue.
4. **MEDIUM-4**: No ILlmRetryQueue interface — callers coupled to concrete class.
5. **MEDIUM-5**: Hardcoded 10s sweep interval — should be configurable.
6. **MEDIUM-6**: Dispose ordering — `_drainSignal.Dispose()` before `base.Dispose()` risks ObjectDisposedException.
7. **MEDIUM-7**: Caller cancellation during retry produces faulted TCS instead of cancelled TCS.
8. **MEDIUM-8**: No test for ExecuteAsync lifecycle.
9. **MEDIUM-9**: No test for OperationCanceledException during retry.

## LOW
1. **LOW-2**: IList<ChatMessage> instead of IReadOnlyList (but IChatClient takes IList)
2. **LOW-3**: Two types in one file (QueuedLlmRequest + LlmRetryQueue)
3. **LOW-4/5**: No concurrent or dispose tests
