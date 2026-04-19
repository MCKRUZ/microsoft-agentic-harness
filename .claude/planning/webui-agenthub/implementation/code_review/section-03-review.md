# Code Review — section-03-conversation-store

## Summary
Clean implementation. Security invariants are correct. Two auto-fixable issues and one question for the user.

---

## Findings

### MEDIUM — `GetConversation` / `DeleteConversation` log the victim's `OwnerId`
**File:** `Controllers/AgentsController.cs` lines 78, 94

The warning logs include `{OwnerId}` — the user ID of the conversation owner who is NOT the caller. For a POC this is likely acceptable, but in production this leaks one user's Azure AD OID into logs that another user's request generated.

**Options:** Remove `OwnerId` from the log, or keep it and document it.

---

### LOW — `User.GetUserId()` called twice in ownership checks
**File:** `Controllers/AgentsController.cs` lines 76+78 and 92+94

`User.GetUserId()` throws `InvalidOperationException` if the OID claim is absent. Calling it twice in the same code path is wasteful and makes the exception risk less obvious. Cache to a local variable.

**Auto-fix:** Cache to `var userId = User.GetUserId();` before the ownership check.

---

### LOW — `AppendMessageAsync` throws raw `FileNotFoundException` for missing conversation
**File:** `Services/FileSystemConversationStore.cs` line 426

If `conversationId` is valid but the file doesn't exist, `File.ReadAllTextAsync` throws `FileNotFoundException` with a raw path in the message. The controller doesn't catch this, so ASP.NET Core returns a 500 with no context. Should check existence and throw a more descriptive exception (or return early).

**Auto-fix:** Add a file-existence check before `ReadAllTextAsync` in `AppendMessageAsync`.

---

### INFO — Path traversal check is correct and well-tested
The `ResolveAndValidatePath` pattern (`GetFullPath` + `StartsWith(_basePath + separator)`) is the correct approach on Windows. The test covers both `../` and absolute path segments. Good.

### INFO — Singleton SemaphoreSlim not disposed
`SemaphoreSlim` implements `IDisposable` but `FileSystemConversationStore` doesn't dispose it. For a singleton registered for the application lifetime this is acceptable — it will be GC'd on process exit.

### INFO — Controller route refactor is backward-compatible
`[Route("api/[controller]")]` → `[Route("api")]` with explicit `[HttpGet("agents")]` produces the same `/api/agents` path. Existing `CoreSetupTests` will pass unmodified. ✓
