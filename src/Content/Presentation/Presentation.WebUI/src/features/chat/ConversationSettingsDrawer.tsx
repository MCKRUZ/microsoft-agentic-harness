import { useEffect, useState } from 'react';
import { X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { useDeploymentsQuery } from '@/features/config/useConfigQuery';
import type { ConversationSettingsInput } from '@/hooks/useAgentHub';

interface ConversationSettingsDrawerProps {
  open: boolean;
  onClose: () => void;
  initial: ConversationSettingsInput;
  onSave: (settings: ConversationSettingsInput) => Promise<void>;
}

const TEMP_MIN = 0;
const TEMP_MAX = 2;
const TEMP_STEP = 0.1;

export function ConversationSettingsDrawer({
  open,
  onClose,
  initial,
  onSave,
}: ConversationSettingsDrawerProps) {
  const deployments = useDeploymentsQuery();
  const [deployment, setDeployment] = useState<string>(initial.deploymentName ?? '');
  const [temperature, setTemperature] = useState<string>(
    initial.temperature != null ? String(initial.temperature) : '',
  );
  const [systemPrompt, setSystemPrompt] = useState<string>(initial.systemPromptOverride ?? '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setDeployment(initial.deploymentName ?? '');
    setTemperature(initial.temperature != null ? String(initial.temperature) : '');
    setSystemPrompt(initial.systemPromptOverride ?? '');
    setError(null);
  }, [open, initial.deploymentName, initial.temperature, initial.systemPromptOverride]);

  if (!open) return null;

  const deploymentOptions = deployments.data
    ? deployments.data.deployments.length > 0
      ? deployments.data.deployments
      : [deployments.data.defaultDeployment]
    : [];

  const parseTemperature = (raw: string): { value: number | null; error: string | null } => {
    const trimmed = raw.trim();
    if (trimmed === '') return { value: null, error: null };
    const n = Number(trimmed);
    if (!Number.isFinite(n)) return { value: null, error: 'Temperature must be a number.' };
    if (n < TEMP_MIN || n > TEMP_MAX) {
      return { value: null, error: `Temperature must be between ${TEMP_MIN} and ${TEMP_MAX}.` };
    }
    return { value: n, error: null };
  };

  const handleSave = async (): Promise<void> => {
    const parsed = parseTemperature(temperature);
    if (parsed.error) {
      setError(parsed.error);
      return;
    }
    setError(null);
    setSaving(true);
    try {
      await onSave({
        deploymentName: deployment.trim() === '' ? null : deployment.trim(),
        temperature: parsed.value,
        systemPromptOverride: systemPrompt.trim() === '' ? null : systemPrompt,
      });
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save settings.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <div
        className="fixed inset-0 bg-black/30 z-40"
        onClick={onClose}
        aria-hidden="true"
      />
      <aside
        role="dialog"
        aria-label="Conversation settings"
        aria-modal="true"
        className="fixed right-0 top-0 h-full w-[360px] bg-background border-l shadow-xl z-50 flex flex-col"
      >
        <header className="flex items-center justify-between px-4 py-3 border-b">
          <h2 className="text-sm font-semibold">Conversation settings</h2>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close settings"
            className="text-muted-foreground hover:text-foreground"
          >
            <X size={16} />
          </button>
        </header>

        <div className="flex-1 overflow-auto p-4 space-y-4 text-sm">
          <div className="space-y-1">
            <label htmlFor="setting-deployment" className="font-medium block">
              Model / deployment
            </label>
            {deployments.isLoading ? (
              <div className="text-xs text-muted-foreground">Loading deployments...</div>
            ) : deployments.isError ? (
              <div className="text-xs text-destructive">Failed to load deployments.</div>
            ) : (
              <select
                id="setting-deployment"
                value={deployment}
                onChange={(e) => { setDeployment(e.target.value); }}
                className="w-full h-9 rounded border bg-background px-2"
              >
                <option value="">Use agent default</option>
                {deploymentOptions.map((d) => (
                  <option key={d} value={d}>{d}</option>
                ))}
              </select>
            )}
            <p className="text-xs text-muted-foreground">
              Override the deployment for this conversation.
            </p>
          </div>

          <div className="space-y-1">
            <label htmlFor="setting-temperature" className="font-medium block">
              Temperature
            </label>
            <div className="flex items-center gap-2">
              <input
                id="setting-temperature"
                type="range"
                min={TEMP_MIN}
                max={TEMP_MAX}
                step={TEMP_STEP}
                value={temperature === '' ? 1 : Number(temperature)}
                onChange={(e) => { setTemperature(e.target.value); }}
                className="flex-1"
                aria-label="Temperature slider"
              />
              <input
                type="text"
                inputMode="decimal"
                value={temperature}
                onChange={(e) => { setTemperature(e.target.value); }}
                placeholder="auto"
                className="w-16 h-8 rounded border bg-background px-2 text-right"
                aria-label="Temperature value"
              />
            </div>
            <p className="text-xs text-muted-foreground">
              Leave blank to use the agent default. Range {TEMP_MIN}\u2013{TEMP_MAX}.
            </p>
          </div>

          <div className="space-y-1">
            <label htmlFor="setting-system-prompt" className="font-medium block">
              System prompt override
            </label>
            <Textarea
              id="setting-system-prompt"
              value={systemPrompt}
              onChange={(e) => { setSystemPrompt(e.target.value); }}
              placeholder="Additional context appended to agent instructions..."
              rows={6}
            />
            <p className="text-xs text-muted-foreground">
              Appended to the agent&rsquo;s built-in instructions for this conversation.
            </p>
          </div>

          {error && <p className="text-xs text-destructive">{error}</p>}
        </div>

        <footer className="flex justify-end gap-2 px-4 py-3 border-t">
          <Button type="button" variant="ghost" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button type="button" onClick={() => { void handleSave(); }} disabled={saving}>
            {saving ? 'Saving...' : 'Save'}
          </Button>
        </footer>
      </aside>
    </>
  );
}
