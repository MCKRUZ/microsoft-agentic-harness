/** Field control types the form widget can render. Kept in sync with RenderFormTool's server whitelist. */
export type FormFieldType = 'text' | 'textarea' | 'number' | 'select' | 'checkbox' | 'date';

const ALLOWED_TYPES: readonly FormFieldType[] = ['text', 'textarea', 'number', 'select', 'checkbox', 'date'];

/** A single validated form field. */
export interface FormFieldSpec {
  name: string;
  label?: string;
  type: FormFieldType;
  required?: boolean;
  /** Present (non-empty) only for `select`. */
  options?: string[];
}

/** A validated form specification. */
export interface FormSpec {
  title?: string;
  submitLabel?: string;
  fields: FormFieldSpec[];
}

/** Outcome of validating raw `render_form` arguments at the client trust boundary. */
export type FormArgsResult =
  | { ok: true; value: FormSpec }
  | { ok: false; reason: string };

/**
 * Validates raw agent-supplied form arguments and coerces them into a typed {@link FormSpec}. Fields
 * with an unknown `type`, a missing `name`, or (for `select`) no `options` are dropped rather than
 * rendered — the field-type whitelist is the client trust boundary mirroring the server-side check in
 * RenderFormTool. Agent output is untrusted, so this runs both when deciding the tool acknowledgement
 * and again at render time.
 */
export function parseFormArgs(args: Record<string, unknown>): FormArgsResult {
  const rawFields = Array.isArray(args.fields) ? args.fields : null;
  if (!rawFields || rawFields.length === 0) return { ok: false, reason: 'The form has no fields.' };

  const fields: FormFieldSpec[] = [];
  const seen = new Set<string>();
  for (const raw of rawFields) {
    if (typeof raw !== 'object' || raw === null) continue;
    const f = raw as Record<string, unknown>;

    const name = typeof f.name === 'string' ? f.name.trim() : '';
    const type = typeof f.type === 'string' ? f.type : '';
    // Duplicate names would collide in the values map and produce duplicate React keys — keep the first.
    if (!name || seen.has(name) || !ALLOWED_TYPES.includes(type as FormFieldType)) continue;

    const field: FormFieldSpec = { name, type: type as FormFieldType };
    if (typeof f.label === 'string') field.label = f.label;
    if (typeof f.required === 'boolean') field.required = f.required;

    if (type === 'select') {
      const options = Array.isArray(f.options)
        ? f.options.filter((o): o is string => typeof o === 'string')
        : [];
      if (options.length === 0) continue; // a select with no options is unusable — drop it
      field.options = options;
    }

    seen.add(name);
    fields.push(field);
  }

  if (fields.length === 0) return { ok: false, reason: 'The form has no valid fields.' };

  const spec: FormSpec = { fields };
  if (typeof args.title === 'string') spec.title = args.title;
  if (typeof args.submitLabel === 'string') spec.submitLabel = args.submitLabel;
  return { ok: true, value: spec };
}

/** Formats submitted values into a readable message the agent receives as the user's next turn. */
export function formatSubmission(spec: FormSpec, values: Record<string, string | boolean>): string {
  const lines = spec.fields
    .map((field) => {
      const raw = values[field.name];
      const label = field.label ?? field.name;
      if (field.type === 'checkbox') return `- ${label}: ${raw === true ? 'Yes' : 'No'}`;
      const text = typeof raw === 'string' ? raw.trim() : '';
      return text ? `- ${label}: ${text}` : null; // omit empty optional fields
    })
    .filter((line): line is string => line !== null);

  return `Here are my answers:\n${lines.join('\n')}`;
}
