export const HUB_EVENTS = {
  TokenReceived: 'TokenReceived',
  TurnComplete: 'TurnComplete',
  ToolCalled: 'ToolCalled',
  ToolResult: 'ToolResult',
  BudgetWarning: 'BudgetWarning',
  MetricsUpdate: 'MetricsUpdate',
  Error: 'Error',
  ConversationStarted: 'ConversationStarted',
} as const;

export type HubEventName = (typeof HUB_EVENTS)[keyof typeof HUB_EVENTS];
