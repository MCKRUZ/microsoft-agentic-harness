import { describe, it, expect } from 'vitest';
import { HUB_EVENTS } from './eventTypes';

/**
 * Regression for solution-review finding #48: the dashboard registered SignalR
 * handlers for five event names the server never emits ('ToolCalled',
 * 'ToolResult', 'BudgetWarning', 'MetricsUpdate', 'ConversationStarted'). Those
 * handlers could never fire, so the telemetry ring buffer stayed permanently
 * empty while the realtime layer looked wired.
 *
 * The server's authoritative event vocabulary is pinned by the
 * `AgentTelemetryHub.Event*` constants in
 * Presentation.AgentHub/Hubs/AgentTelemetryHub.cs. Every name HUB_EVENTS
 * subscribes to MUST be a member of that set, otherwise the handler is dead.
 */

// Mirrors AgentTelemetryHub.Event* — the only names the server is allowed to
// push. Kept in sync by hand because the C# host and the TS dashboard are
// separate build units with no shared schema artifact.
const SERVER_EMITTED_EVENT_NAMES = new Set<string>([
  'TokenReceived',
  'TurnComplete',
  'ToolCallStarted',
  'ToolCallCompleted',
  'SpanReceived',
  'Error',
  'HistoryTruncated',
  'EvalRunCompleted',
  'ContextSnapshot',
]);

// The exact phantom names the fix removed — none of these is emitted by the hub.
const PHANTOM_EVENT_NAMES = [
  'ToolCalled',
  'ToolResult',
  'BudgetWarning',
  'MetricsUpdate',
  'ConversationStarted',
] as const;

describe('HUB_EVENTS solution-review fix', () => {
  it('subscribes only to event names the server actually emits', () => {
    const subscribed = Object.values(HUB_EVENTS);
    const unknown = subscribed.filter(
      (name) => !SERVER_EMITTED_EVENT_NAMES.has(name),
    );

    expect(unknown).toEqual([]);
  });

  it('no longer registers the phantom event names from finding #48', () => {
    const subscribed = Object.values(HUB_EVENTS) as string[];

    for (const phantom of PHANTOM_EVENT_NAMES) {
      expect(subscribed).not.toContain(phantom);
    }
  });
});
