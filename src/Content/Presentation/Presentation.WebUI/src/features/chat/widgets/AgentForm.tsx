import { useMemo, useState, type FormEvent } from 'react';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Button } from '@/components/ui/button';
import { useSendUserMessage } from '@/hooks/useSendUserMessage';
import { parseFormArgs, formatSubmission, type FormFieldSpec, type FormSpec } from './formTypes';

type FieldValue = string | boolean;
type FormValues = Record<string, FieldValue>;

function initialValues(spec: FormSpec): FormValues {
  const values: FormValues = {};
  for (const field of spec.fields) values[field.name] = field.type === 'checkbox' ? false : '';
  return values;
}

function isBlank(field: FormFieldSpec, value: FieldValue): boolean {
  return field.type === 'checkbox' ? value !== true : String(value ?? '').trim() === '';
}

/** Renders one field control by type. `select` uses a native element so agent-generated option lists
 *  render without the heavier composed Select primitive. */
function Field({
  field,
  value,
  onChange,
  disabled,
}: {
  field: FormFieldSpec;
  value: FieldValue;
  onChange: (name: string, value: FieldValue) => void;
  disabled: boolean;
}) {
  const id = `agent-form-${field.name}`;
  const label = field.label ?? field.name;

  if (field.type === 'checkbox') {
    return (
      <label htmlFor={id} className="flex items-center gap-2 text-xs font-medium text-foreground">
        <input
          type="checkbox"
          id={id}
          name={field.name}
          checked={value === true}
          disabled={disabled}
          onChange={(e) => { onChange(field.name, e.target.checked); }}
          className="h-4 w-4 rounded border-border accent-primary"
        />
        {label}
      </label>
    );
  }

  const labelEl = (
    <label htmlFor={id} className="text-xs font-medium text-foreground">
      {label}{field.required && <span className="text-destructive"> *</span>}
    </label>
  );

  let control: React.ReactNode;
  switch (field.type) {
    case 'textarea':
      control = (
        <Textarea id={id} name={field.name} value={String(value)} rows={3} disabled={disabled}
          onChange={(e) => { onChange(field.name, e.target.value); }} />
      );
      break;
    case 'select':
      control = (
        <select id={id} name={field.name} value={String(value)} disabled={disabled}
          onChange={(e) => { onChange(field.name, e.target.value); }}
          className="flex h-9 w-full rounded-md border border-border bg-background px-3 py-1 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-ring disabled:opacity-50">
          <option value="" disabled>Select…</option>
          {field.options?.map((opt) => <option key={opt} value={opt}>{opt}</option>)}
        </select>
      );
      break;
    default:
      // text, number, and date all render a single-line Input; the input type follows the field
      // (only text/number/date reach here — checkbox/textarea/select are handled above).
      control = (
        <Input id={id} name={field.name} type={field.type} value={String(value)} disabled={disabled}
          onChange={(e) => { onChange(field.name, e.target.value); }} />
      );
  }

  return <div className="space-y-1">{labelEl}{control}</div>;
}

/**
 * Renders an agent-supplied interactive form inline in the transcript. On submit it does NOT return
 * through the tool round-trip (that already acknowledged synchronously); it sends the answers as the
 * user's next message via {@link useSendUserMessage}, so the agent continues in a fresh turn. Locks
 * after a successful submit to prevent double-sending. Re-validates the spec at render time (agent
 * output is untrusted); an invalid spec shows a fallback instead of a broken form.
 */
export function AgentForm({ args }: { args: Record<string, unknown> }) {
  const send = useSendUserMessage();
  const parsed = useMemo(() => parseFormArgs(args), [args]);
  const [values, setValues] = useState<FormValues>(() => (parsed.ok ? initialValues(parsed.value) : {}));
  const [submitted, setSubmitted] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!parsed.ok) {
    return (
      <div data-testid="agent-form-fallback"
        className="rounded-lg border border-border/50 bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
        {parsed.reason}
      </div>
    );
  }
  const spec = parsed.value;

  const setValue = (name: string, value: FieldValue): void => {
    setValues((prev) => ({ ...prev, [name]: value }));
  };

  const handleSubmit = (e: FormEvent): void => {
    e.preventDefault();
    if (submitted) return;

    const missing = spec.fields.filter((f) => f.required && isBlank(f, values[f.name]));
    if (missing.length > 0) {
      setError(`Please fill in: ${missing.map((f) => f.label ?? f.name).join(', ')}`);
      return;
    }

    if (!send(formatSubmission(spec, values))) {
      setError('There is no active conversation to submit to.');
      return;
    }
    setError(null);
    setSubmitted(true);
  };

  return (
    <form onSubmit={handleSubmit} data-testid="agent-form"
      className="rounded-lg border border-border/50 bg-card/50 p-3 space-y-3 max-w-md">
      {spec.title && <div className="text-sm font-medium text-foreground">{spec.title}</div>}
      {spec.fields.map((field) => (
        <Field key={field.name} field={field} value={values[field.name]} onChange={setValue} disabled={submitted} />
      ))}
      {error && <p data-testid="agent-form-error" className="text-xs text-destructive">{error}</p>}
      <div className="flex items-center gap-2">
        <Button type="submit" size="sm" disabled={submitted}>
          {submitted ? 'Submitted' : (spec.submitLabel ?? 'Submit')}
        </Button>
        {submitted && <span className="text-xs text-muted-foreground">Your answers were sent.</span>}
      </div>
    </form>
  );
}
