# Interview Transcript

## Round 1

### Q1: How should users authenticate in the WebUI?

**Answer:** Login screen

A simple username/password form in the WebUI. AgentHub has a POST /auth/token endpoint that issues a JWT. Credentials stored in config/user-secrets for the POC.

*Updated in Round 3:* User selected Windows/Azure AD auth — meaning MSAL-based authentication using Azure Active Directory. This supersedes the simple token endpoint approach. The WebUI uses `@azure/msal-react`, AgentHub uses `Microsoft.Identity.Web`.

---

### Q2: Should conversations persist across browser sessions/reloads?

**Answer:** Server-side persistence

Conversation history lives on the server and survives browser refresh. (Storage backing clarified in Round 2: file-backed JSON, matching the existing `FileSystemToolResultStore` pattern.)

---

### Q3: Should the chat UI let users pick which agent to use?

**Answer:** Agent selector in UI

Dropdown to pick from available agents. Requires AgentHub to expose `GET /api/agents` listing configured agents from appsettings.

---

### Q4: Should the Traces tab show conversation-scoped or global spans?

**Answer:** Both (tabbed or toggle)

Both views available. (UI pattern clarified in Round 3: separate "My Traces" and "All Traces" tabs within the telemetry panel.)

---

## Round 2

### Q5: Where should conversation history live on the server?

**Answer:** File-backed

Use the existing `FileSystemToolResultStore` pattern — each conversation serialized to JSON on disk. Matches existing Infrastructure.AI patterns and survives server restarts.

---

### Q6: When a user clicks 'Invoke' on a tool in the Tools tab, how should it work?

**Answer:** Both modes

A toggle: 'Direct' (calls `IMcpToolProvider` directly via `POST /api/mcp/tools/{name}/invoke`) vs 'Via agent' (sends a message to the agent asking it to use the tool). Most flexible; demonstrates both invocation paths.

---

### Q7: How should SignalR telemetry be broadcast across multiple connected clients?

**Answer:** Per-connection groups (Recommended)

Each chat session broadcasts traces only to the client that started it. Multiple users don't see each other's traces. Implemented with SignalR Groups keyed by `conversationId`.

---

## Round 3

### Q8: For the login screen, what credential model should the POC use?

**Answer:** Windows/Azure AD auth

Real enterprise authentication via Microsoft Identity Platform. The WebUI uses `@azure/msal-react` + `@azure/msal-browser`. AgentHub uses `Microsoft.Identity.Web` with Azure AD token validation. Config placeholders for TenantId, ClientId, and Audience — developers fill in their own Azure AD app registration.

---

### Q9: For the trace scope toggle, where should it live?

**Answer:** Separate tabs: 'My Traces' and 'All Traces'

Within the right telemetry panel, the tab bar becomes: **[My Traces] [All Traces] [Tools] [Resources] [Prompts]**. "My Traces" shows spans filtered to the active `conversationId`. "All Traces" shows the global firehose from the server session.

---

## Summary of Decisions

| Decision | Choice |
|---|---|
| Authentication | Azure AD / Microsoft Identity Platform (MSAL in React, Microsoft.Identity.Web in .NET) |
| Conversation persistence | File-backed JSON on AgentHub server |
| Agent selection | Dropdown UI, backed by `GET /api/agents` |
| Trace scope | Two tabs: "My Traces" (conversation-scoped) + "All Traces" (global) |
| Tool invocation | Toggle between Direct (`IMcpToolProvider`) and Via Agent (chat pipeline) |
| SignalR broadcast | Per-connection groups keyed by `conversationId` |
| Ports | AgentHub = 5001, Vite dev server = 5173 |
| Real-time | SignalR (`@microsoft/signalr`) |
| UI library | shadcn/ui + Tailwind CSS v4 |
