# Code Review Interview — Section 02: AgentHub Core

## Review Triage

| Finding | Severity | Action |
|---------|----------|--------|
| PostConfigure replaces entire JwtBearerEvents, discarding M.I.W handlers | HIGH | Auto-fix |
| Captive dependency comment inaccurate | HIGH | Auto-fix |
| Directory.SetCurrentDirectory is process-global (parallel test risk) | MEDIUM | Let go (no parallel tests in this project; section-07 will address) |
| Rate limit test fragility if later tests POST to /api/mcp/tools/* | MEDIUM | Let go (noted; later sections will use separate test factories) |
| 10 undisposed HttpResponseMessage in rate limit loop | MEDIUM | Auto-fix |
| Hardcoded Kestrel port 5001 | LOW | Let go (deferred to infrastructure/deployment config) |
| ConversationsPath traversal risk | LOW | Let go (section-03 will add validation in FileSystemConversationStore) |
| HubSendMessage bucket is global not per-user | LOW | Let go (section-04 wires the hub; will revisit then) |

No user questions were needed — all items were either obvious fixes or clearly deferrable.

## Auto-Fixes Applied

### Fix 1: Chain existing OnMessageReceived instead of replacing Events object

**File:** `DependencyInjection.cs`

**Before:**
```csharp
services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context => { ... }
    };
});
```

**After:**
```csharp
services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Events ??= new JwtBearerEvents();
    var existingOnMessageReceived = options.Events.OnMessageReceived;
    options.Events.OnMessageReceived = async context =>
    {
        if (existingOnMessageReceived != null)
            await existingOnMessageReceived(context);
        // SignalR token extraction...
    };
});
```

**Why:** Replacing the entire `Events` object discards `OnTokenValidated`, `OnAuthenticationFailed`, and other handlers set by `Microsoft.Identity.Web`. Chaining preserves them.

### Fix 2: Correct the captive dependency comment

**File:** `TestWebApplicationFactory.cs`

Updated comment to accurately describe the dependency chain:
`MemoizedPromptComposer (singleton) → IPromptSectionProvider (transient) → IAgentExecutionContext (scoped)`

### Fix 3: Dispose intermediate HttpResponseMessage objects in rate limit test

**File:** `CoreSetupTests.cs`

Changed the rate limit loop to use `using var response` per iteration, capturing only the status code. Prevents socket handle exhaustion in long test runs.

## Decisions Not Made (Deferred)

- `Directory.SetCurrentDirectory` race: Acceptable for section-02; section-07 will redesign test infrastructure using `WebApplicationFactory.WithWebHostBuilder` + content root overrides to eliminate the global state.
- Per-user rate limiting for `HubSendMessage`: Deferred to section-04 when the hub methods are implemented.
