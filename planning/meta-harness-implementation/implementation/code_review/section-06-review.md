# Code Review: Section-06 Agent History Store

## Summary

Solid append-only event log with correct thread-safety primitives, clean separation between interface and implementation, and a well-tested tool adapter. The SemaphoreSlim + Interlocked.Increment combination is the right approach for lock-free sequence allocation with serialized I/O. There are no critical issues. The main concerns are a missing IDisposable on the store class, unguarded Convert.To* calls in the tool, and a few test coverage gaps.

## Findings

### CRITICAL

None.

### HIGH

**[H-1] JsonlAgentHistoryStore does not dispose its SemaphoreSlim**
File: JsonlAgentHistoryStore.cs:42
The class owns a SemaphoreSlim but does not implement IDisposable. Since the store is created via a factory delegate (Func\<ITraceWriter, IAgentHistoryStore\>) and is scoped per execution run, it will be discarded when the run ends -- but the semaphore internal ManualResetEvent (allocated on contention) will not be cleaned up.

Fix -- implement IDisposable (not IAsyncDisposable -- SemaphoreSlim.Dispose() is synchronous):

    public sealed class JsonlAgentHistoryStore : IAgentHistoryStore, IDisposable
    {
        // ... existing code ...
        public void Dispose() => _writeLock.Dispose();
    }

Update IAgentHistoryStore or the factory consumer to call Dispose at run teardown. Since ITraceWriter already implements IAsyncDisposable, the AgentExecutionContextFactory (section 14) should dispose the history store in the same teardown path.

**[H-2] Convert.ToInt64 / Convert.ToInt32 throw on non-numeric input**
File: ReadHistoryTool.cs:93-101
GetLong and GetInt use Convert.ToInt64(v) and Convert.ToInt32(v) which throw FormatException or OverflowException if the parameter value is a non-numeric string (e.g., "abc"). Since parameters come from LLM-generated JSON, malformed values are plausible.

Fix -- use TryParse-based extraction:

    private static long GetLong(IReadOnlyDictionary<string, object?> p, string key) =>
        p.TryGetValue(key, out var v) && v is not null
        && long.TryParse(v.ToString(), out var result)
            ? result
            : 0L;

    private static int GetInt(IReadOnlyDictionary<string, object?> p, string key, int defaultValue) =>
        p.TryGetValue(key, out var v) && v is not null
        && int.TryParse(v.ToString(), out var result)
            ? result
            : defaultValue;

### MEDIUM

**[M-1] Partial line read race in QueryAsync**
File: JsonlAgentHistoryStore.cs:82-136
QueryAsync opens the file with FileShare.ReadWrite and reads while AppendAsync may be writing. If a reader enters between File.AppendAllTextAsync writing some bytes and completing the full line, ReadLineAsync could return a partial JSON line. The catch (JsonException) { continue; } on line 112-115 handles this gracefully -- the partial line is skipped. However, the event is silently lost from the query results.

This is acceptable for a POC, but worth documenting. In production, you would want a retry or a reader-side fence.

**[M-2] Sequence number ordering not guaranteed in file**
File: JsonlAgentHistoryStore.cs:57-79
Interlocked.Increment happens before _writeLock.WaitAsync. Thread A could get sequence 5, thread B gets sequence 6, but B acquires the lock first and writes sequence 6 before sequence 5 in the file. The QueryAsync filter (evt.Sequence \<= query.Since) works correctly regardless of file order (it filters on sequence value, not position), so this is functionally correct. But consumers reading the raw JSONL file with external tools might expect ordered sequences. Worth a doc comment.

**[M-3] ReadHistoryTool is not registered as a keyed DI tool**
File: Infrastructure.AI/DependencyInjection.cs
FileSystemTool is registered as services.AddKeyedSingleton\<ITool\>(FileSystemTool.ToolName, ...) but ReadHistoryTool has no keyed registration. The tool depends on IAgentHistoryStore which is run-scoped (created via factory), so it cannot be a simple singleton. This means either:
- The tool needs to be created per-run alongside the history store (in section 14 context factory), or
- The DI registration needs a factory that captures the run-scoped store.

This is likely intentional (deferred to section 14), but should be tracked. Without registration, the tool is dead code from DI perspective.

**[M-4] No test for TurnId filter**
File: JsonlAgentHistoryStoreTests.cs
Tests cover EventType, ToolName, Since, and Limit filters, but TurnId filtering is not tested despite being implemented at line 127-129 of the store. Add a test:

    [Fact]
    public async Task QueryAsync_FilterByTurnId_ReturnsMatchingOnly()
    {
        var store = CreateStore();
        const string runId = "run-filter-turn";
        await store.AppendAsync(BuildEvent(runId: runId, turnId: "turn-1"));
        await store.AppendAsync(BuildEvent(runId: runId, turnId: "turn-2"));
        await store.AppendAsync(BuildEvent(runId: runId, turnId: "turn-1"));

        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
        {
            ExecutionRunId = runId,
            TurnId = "turn-1"
        }));

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(e => e.TurnId.Should().Be("turn-1"));
    }

**[M-5] No test for corrupted JSONL lines**
File: JsonlAgentHistoryStoreTests.cs
The store gracefully handles JsonException on deserialization (line 112-115), but no test validates this behavior. Add a test that manually writes a corrupted line to the file before querying.

### LOW / Nitpick

**[L-1] Duplicate JsonSerializerOptions instances**
Files: JsonlAgentHistoryStore.cs:30-38, ReadHistoryTool.cs:29-33
Both classes define their own static readonly JsonSerializerOptions with SnakeCaseLower. This works but is duplicated configuration. Consider extracting a shared HistoryJsonOptions static class.

**[L-2] DecisionLogQuery.Since default of 0 -- semantic clarity**
File: IAgentHistoryStore.cs:70
Since sequences start at 1 (Interlocked.Increment from 0 yields 1), Since = 0 means "return all events." The XML doc on line 67-68 explains this. The exclusive semantics (Sequence \> Since) are correct and well-documented.

**[L-3] ReadHistoryTool.ExecuteAsync returns "[]" for missing execution_run_id**
File: ReadHistoryTool.cs:71-72
Returning success with "[]" for a missing required parameter is debatable. An argument could be made for ToolResult.Fail("execution_run_id is required"). However, the current behavior is safe for the LLM (it sees an empty result set), and the spec says "safe for unknown run IDs", so this is a design choice, not a bug.

**[L-4] AgentDecisionEvent uses string for EventType instead of an enum**
File: IAgentHistoryStore.cs:24
The four known values (tool_call, tool_result, decision, observation) could be a StringEnum or constants class. Using raw strings risks typos at call sites. Low priority since this is a POC and the domain is still evolving.

**[L-5] Concurrent append test does not verify sequence uniqueness**
File: JsonlAgentHistoryStoreTests.cs:99-126
AppendAsync_ConcurrentAppends_DoNotCorruptFile verifies line count (50) and valid JSON, but does not assert that all 50 sequence numbers are unique. Add:

    var sequences = lines.Select(l =>
        JsonSerializer.Deserialize<AgentDecisionEvent>(l, options)!.Sequence).ToList();
    sequences.Should().OnlyHaveUniqueItems();

## Verdict

**APPROVE WITH FIXES**

Fix H-1 (add IDisposable) and H-2 (guard Convert.To* with TryParse) before merging. The MEDIUM items (M-4/M-5 test gaps, M-3 DI registration) should be addressed in follow-up but do not block.
