import type { ReactNode } from 'react';
import { AgentImage } from './AgentImage';
import { AgentForm } from './AgentForm';
import { AgentTable } from './AgentTable';
import { UnsupportedWidget } from './UnsupportedWidget';
import { parseImageArgs, type AgentWidget } from './types';
import { parseFormArgs } from './formTypes';
import { parseTableArgs } from './tableTypes';
import catalog from './widgetCatalog.json';

/** Outcome of validating a widget's raw args: ok → it renders and `ack` is returned to the agent;
 *  not ok → `reason` is returned and no widget is shown. */
type ValidationResult = { ok: true } | { ok: false; reason: string };

/** A widget tool name declared in the shared catalogue, `widgetCatalog.json`. */
type WidgetType = keyof typeof catalog.widgets;

/** The version a saved widget with no version field was produced at: every widget was at 1 when
 *  versioning was introduced. Mirrors `WidgetSpec.LegacyVersion` on the server. */
const LEGACY_VERSION = 1;

/**
 * Everything the transcript and the stream handler need for one inline widget: how its result renders,
 * how to validate the agent's raw args at the client trust boundary, the acknowledgement the agent
 * observes once it is displayed, and the argument-shape version it renders. This is the generative-UI
 * trust boundary: the agent can only summon a registered widget, with validated args.
 */
export interface WidgetDefinition {
  render: (args: Record<string, unknown>) => ReactNode;
  validate: (args: Record<string, unknown>) => ValidationResult;
  ack: string;
  version: number;
}

/**
 * One entry per widget in `widgetCatalog.json`. Typed as a record over the catalogue's names, so the
 * compiler refuses a catalogue widget with no entry here and an entry the catalogue does not declare.
 * Adding a widget means a catalogue entry, an entry here, and the matching server tool and
 * `ClientWidgetCatalog` line (ClientWidgetCatalogParityTests checks those two).
 */
const DEFINITIONS: Record<WidgetType, Omit<WidgetDefinition, 'version'>> = {
  render_image: {
    render: (args) => <AgentImage args={args} />,
    validate: parseImageArgs,
    ack: 'Displayed the image to the user.',
  },
  render_form: {
    render: (args) => <AgentForm args={args} />,
    validate: parseFormArgs,
    ack: 'Displayed the form to the user; their answers will arrive as their next message.',
  },
  render_table: {
    render: (args) => <AgentTable args={args} />,
    validate: parseTableArgs,
    ack: 'Displayed the table to the user.',
  },
};

// A Map (not an object) so an agent-influenced widget type can never resolve to an inherited member
// like "constructor" and get invoked — lookups are prototype-safe by construction.
const WIDGET_REGISTRY = new Map<string, WidgetDefinition>(
  (Object.keys(DEFINITIONS) as WidgetType[]).map((type) => [
    type,
    { ...DEFINITIONS[type], version: catalog.widgets[type] },
  ]),
);

/** The widget definition for a client tool name, or undefined if it is not a widget tool. */
export function getWidget(type: string): WidgetDefinition | undefined {
  return WIDGET_REGISTRY.get(type);
}

/** Every registered widget tool name. */
export function registeredWidgetTypes(): readonly string[] {
  return [...WIDGET_REGISTRY.keys()];
}

/**
 * Renders an inline widget by tool name. A widget this app cannot render — a type it no longer has, or a
 * saved version whose argument shape has since changed — shows a placeholder rather than nothing, so a
 * saved transcript degrades visibly instead of silently losing content. Never throws: the transcript must
 * not crash on an unexpected agent tool call.
 */
export function renderWidget(widget: AgentWidget): ReactNode {
  const definition = WIDGET_REGISTRY.get(widget.type);
  if (!definition || (widget.version ?? LEGACY_VERSION) !== definition.version) {
    return <UnsupportedWidget />;
  }
  return definition.render(widget.args);
}
