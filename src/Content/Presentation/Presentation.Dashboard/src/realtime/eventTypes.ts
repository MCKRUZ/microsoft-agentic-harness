// Server-to-client SignalR event names this dashboard registers handlers for.
// Every entry MUST match a name the server actually emits — see the
// `AgentTelemetryHub.Event*` constants in
// Presentation.AgentHub/Hubs/AgentTelemetryHub.cs. Registering a handler for a
// name the server never sends is silently dead: the handler never fires, the
// telemetry ring buffer never fills, and the UI looks wired while doing nothing.
//
// Removed (2026-06-11): 'ToolCalled', 'ToolResult', 'BudgetWarning',
// 'MetricsUpdate', and 'ConversationStarted' were phantom names with no
// corresponding server emission, so their handlers could never fire. The hub's
// real tool/span vocabulary is 'ToolCallStarted', 'ToolCallCompleted', and
// 'SpanReceived'; surfacing those into the ring buffer additionally requires
// widening the `TelemetryEvent['type']` union in api/types.ts and is tracked
// separately (cross-file rewiring outside this file's scope).
export const HUB_EVENTS = {
  TokenReceived: 'TokenReceived',
  TurnComplete: 'TurnComplete',
  Error: 'Error',
  // PR 3: Foresight per-turn context-window snapshot. Routed exclusively to
  // sessionSnapshotsStore (NOT the generic telemetryStore buffer — see the
  // early return in useTelemetryStream) so the session-detail timeline can
  // read by conversation id without doubling the largest event type into the
  // ring buffer.
  ContextSnapshot: 'ContextSnapshot',
} as const;

export type HubEventName = (typeof HUB_EVENTS)[keyof typeof HUB_EVENTS];
