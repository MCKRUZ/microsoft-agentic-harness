import type { ReactNode } from 'react';
import { AgentImage } from './AgentImage';
import { AgentForm } from './AgentForm';
import { AgentTable } from './AgentTable';
import { parseImageArgs, type AgentWidget } from './types';
import { parseFormArgs } from './formTypes';
import { parseTableArgs } from './tableTypes';

/** Outcome of validating a widget's raw args: ok → it renders and `ack` is returned to the agent;
 *  not ok → `reason` is returned and no widget is shown. */
type ValidationResult = { ok: true } | { ok: false; reason: string };

/**
 * Everything the transcript and the stream handler need for one inline widget: how its result renders,
 * how to validate the agent's raw args at the client trust boundary, and the acknowledgement the agent
 * observes once it is displayed. Adding a widget is one entry in {@link WIDGET_REGISTRY} — the stream
 * handler (generic) and the transcript renderer both dispatch off it, so nothing else changes. This is
 * the generative-UI trust boundary: the agent can only summon a registered widget, with validated args.
 */
export interface WidgetDefinition {
  render: (args: Record<string, unknown>) => ReactNode;
  validate: (args: Record<string, unknown>) => ValidationResult;
  ack: string;
}

// A Map (not an object) so an agent-influenced widget type can never resolve to an inherited member
// like "constructor" and get invoked — lookups are prototype-safe by construction.
const WIDGET_REGISTRY = new Map<string, WidgetDefinition>([
  ['render_image', {
    render: (args) => <AgentImage args={args} />,
    validate: parseImageArgs,
    ack: 'Displayed the image to the user.',
  }],
  ['render_form', {
    render: (args) => <AgentForm args={args} />,
    validate: parseFormArgs,
    ack: 'Displayed the form to the user; their answers will arrive as their next message.',
  }],
  ['render_table', {
    render: (args) => <AgentTable args={args} />,
    validate: parseTableArgs,
    ack: 'Displayed the table to the user.',
  }],
]);

/** The widget definition for a client tool name, or undefined if it is not a widget tool. */
export function getWidget(type: string): WidgetDefinition | undefined {
  return WIDGET_REGISTRY.get(type);
}

/**
 * Renders an inline widget by tool name. An unknown type renders nothing (a safe fallback) rather than
 * throwing — the transcript must never crash on an unexpected agent tool call.
 */
export function renderWidget(widget: AgentWidget): ReactNode {
  return WIDGET_REGISTRY.get(widget.type)?.render(widget.args) ?? null;
}
