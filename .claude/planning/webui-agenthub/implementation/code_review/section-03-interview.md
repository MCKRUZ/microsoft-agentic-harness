# Code Review Interview — section-03-conversation-store

## Decision Log

### [ASK] Log OwnerId in ownership-violation warnings
**Finding:** Warning logs include `{OwnerId}` — the victim user's Azure AD OID.
**User decision:** Keep it — useful for auditing IDOR attempts.
**Applied:** Added `// Log both caller and owner IDs — intentional audit trail for IDOR attempts.` comment in `AgentsController.GetConversation` and `AgentsController.DeleteConversation`.

---

### [AUTO-FIX] Double call to `User.GetUserId()` in ownership checks
**Finding:** `GetConversation` and `DeleteConversation` called `User.GetUserId()` twice — once for the check, once in the log message.
**Applied:** Cached to `var callerId = User.GetUserId();` before the ownership check in both methods.
**File:** `Controllers/AgentsController.cs`

---

### [AUTO-FIX] `AppendMessageAsync` throws raw `FileNotFoundException` for missing conversation
**Finding:** `File.ReadAllTextAsync` threw with a raw OS error message if the file didn't exist.
**Applied:** Added `if (!File.Exists(path)) throw new InvalidOperationException(...)` before the read.
**File:** `Services/FileSystemConversationStore.cs`

---

### [LET GO] `SemaphoreSlim` not disposed
Singleton lifetime — disposed on process exit. No action needed for POC.

### [LET GO] `ListAsync` holds global lock while reading all files
Documented as intentionally simple for POC scale. No action needed.
