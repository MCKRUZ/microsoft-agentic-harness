import type { ReactNode } from 'react';
import { AgentImage } from './AgentImage';
import type { AgentWidget } from './types';

type WidgetRenderer = (args: Record<string, unknown>) => ReactNode;

/**
 * The palette of inline widgets the agent can summon: a client tool name → the component that renders
 * its result. This is the generative-UI trust boundary — the agent selects a widget and supplies its
 * arguments, but can never introduce a component that is not registered here. Add a new widget by
 * registering its tool name and a renderer; nothing else in the transcript path changes.
 */
// NOTE: this maps a tool name to how its result RENDERS. The matching round-trip handler (validate
// args, push the widget message, produce the agent acknowledgement) currently lives in
// useAgentStream's dispatch — a widget must be added in both places until PR2 (render_form) unifies
// them. render_form defers its acknowledgement until user submit, so the unified shape can't be
// designed against render_image's synchronous ack alone; keep the two in sync by hand until then.
// A Map (not an object) so an agent-influenced widget.type can never resolve to an inherited member
// like "constructor" and get invoked — lookups are prototype-safe by construction.
const WIDGET_REGISTRY = new Map<string, WidgetRenderer>([
  ['render_image', (args) => <AgentImage args={args} />],
]);

/**
 * Renders an inline widget by tool name. An unknown type renders nothing (a safe fallback) rather than
 * throwing — the transcript must never crash on an unexpected agent tool call.
 */
export function renderWidget(widget: AgentWidget): ReactNode {
  return WIDGET_REGISTRY.get(widget.type)?.(widget.args) ?? null;
}
