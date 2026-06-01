export const HUB_EVENTS = {
  TokenReceived: 'TokenReceived',
  TurnComplete: 'TurnComplete',
  ToolCalled: 'ToolCalled',
  ToolResult: 'ToolResult',
  BudgetWarning: 'BudgetWarning',
  MetricsUpdate: 'MetricsUpdate',
  Error: 'Error',
  ConversationStarted: 'ConversationStarted',
  // PR 3: Foresight per-turn context-window snapshot. Routed exclusively to
  // sessionSnapshotsStore (NOT the generic telemetryStore buffer — see the
  // early return in useTelemetryStream) so the session-detail timeline can
  // read by conversation id without doubling the largest event type into the
  // ring buffer.
  ContextSnapshot: 'ContextSnapshot',
} as const;

export type HubEventName = (typeof HUB_EVENTS)[keyof typeof HUB_EVENTS];
