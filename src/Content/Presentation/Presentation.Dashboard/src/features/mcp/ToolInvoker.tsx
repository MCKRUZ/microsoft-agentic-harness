import { useState, useMemo } from 'react';
import { Play } from 'lucide-react';
import { useAgentHub } from '@/hooks/useAgentHub';
import { useChatStore } from '@/stores/chatStore';
import { useInvokeTool, type McpTool } from './useMcpQuery';
import { Card, CardContent } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { JsonViewer } from '@/components/ui/json-viewer';
import { cn } from '@/lib/utils';

interface ToolInvokerProps {
  tool: McpTool;
}

type Mode = 'direct' | 'via-agent';

interface ParamDef {
  name: string;
  type: string;
  description?: string;
  required: boolean;
}

function extractParams(schema: Record<string, unknown>): ParamDef[] {
  const properties = schema.properties as Record<string, { type?: string; description?: string }> | undefined;
  const required = (schema.required as string[]) ?? [];
  if (!properties) return [];
  return Object.entries(properties).map(([name, prop]) => ({
    name,
    type: prop.type ?? 'string',
    description: prop.description,
    required: required.includes(name),
  }));
}

function ParamForm({ params, values, onChange }: {
  params: ParamDef[];
  values: Record<string, string>;
  onChange: (name: string, value: string) => void;
}) {
  return (
    <div className="flex flex-col gap-3">
      {params.map((param) => (
        <div key={param.name}>
          <label className="flex items-center gap-1.5 text-xs font-medium mb-1">
            <span className="font-mono">{param.name}</span>
            <Badge variant={param.required ? 'default' : 'outline'} className="text-[9px] h-4">
              {param.type}
            </Badge>
            {param.required && <span className="text-destructive">*</span>}
          </label>
          {param.description && (
            <p className="text-[11px] text-muted-foreground mb-1">{param.description}</p>
          )}
          <Input
            value={values[param.name] ?? ''}
            onChange={(e) => onChange(param.name, e.target.value)}
            placeholder={`Enter ${param.name}...`}
            className="h-7 text-xs font-mono"
          />
        </div>
      ))}
    </div>
  );
}

export function ToolInvoker({ tool }: ToolInvokerProps) {
  const [mode, setMode] = useState<Mode>('direct');
  const [rawInput, setRawInput] = useState('{}');
  const [paramValues, setParamValues] = useState<Record<string, string>>({});
  const [agentResponse, setAgentResponse] = useState<string | null>(null);
  const [agentError, setAgentError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  const conversationId = useChatStore((s) => s.conversationId);
  const { invokeToolViaAgent } = useAgentHub();
  const mutation = useInvokeTool();

  const params = useMemo(() => extractParams(tool.inputSchema), [tool.inputSchema]);
  const useFormMode = params.length > 0;

  const buildArgs = (): Record<string, unknown> | null => {
    if (!useFormMode) {
      try {
        return JSON.parse(rawInput) as Record<string, unknown>;
      } catch {
        setAgentError('Invalid JSON input');
        return null;
      }
    }

    const args: Record<string, unknown> = {};
    for (const param of params) {
      const val = paramValues[param.name]?.trim() ?? '';
      if (!val && param.required) {
        setAgentError(`Required parameter "${param.name}" is missing`);
        return null;
      }
      if (!val) continue;

      if (param.type === 'number' || param.type === 'integer') {
        const num = Number(val);
        if (isNaN(num)) {
          setAgentError(`Parameter "${param.name}" must be a number`);
          return null;
        }
        args[param.name] = num;
      } else if (param.type === 'boolean') {
        args[param.name] = val === 'true' || val === '1';
      } else if (param.type === 'object' || param.type === 'array') {
        try {
          args[param.name] = JSON.parse(val);
        } catch {
          setAgentError(`Parameter "${param.name}" must be valid JSON`);
          return null;
        }
      } else {
        args[param.name] = val;
      }
    }
    return args;
  };

  const handleSubmit = async () => {
    setAgentError(null);
    setAgentResponse(null);

    const args = buildArgs();
    if (!args) return;

    if (mode === 'direct') {
      mutation.mutate({ name: tool.name, args });
    } else {
      if (!conversationId) {
        setAgentError('No active conversation. Send a message in chat first.');
        return;
      }
      setIsSubmitting(true);
      try {
        await invokeToolViaAgent(conversationId, tool.name, args);
        setAgentResponse('Tool invoked via agent. Response will appear in chat.');
      } catch (err) {
        setAgentError(err instanceof Error ? err.message : 'Failed to invoke tool via agent');
      } finally {
        setIsSubmitting(false);
      }
    }
  };

  const hasError = mode === 'direct' ? mutation.isError : !!agentError;
  const errorMessage =
    mode === 'direct'
      ? (mutation.error?.message ?? 'Request failed')
      : agentError;
  const responseData = mode === 'direct' ? mutation.data : agentResponse;
  const isPending = mutation.isPending || isSubmitting;

  return (
    <Card size="sm" className="bg-muted/20">
      <CardContent className="flex flex-col gap-3">
        {/* Mode Toggle */}
        <div className="flex items-center gap-1 p-0.5 bg-muted/50 rounded-md w-fit">
          <button
            type="button"
            onClick={() => setMode('direct')}
            className={cn(
              'px-3 py-1 text-xs rounded font-medium transition-colors',
              mode === 'direct'
                ? 'bg-primary text-primary-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground'
            )}
          >
            Direct
          </button>
          <button
            type="button"
            onClick={() => setMode('via-agent')}
            className={cn(
              'px-3 py-1 text-xs rounded font-medium transition-colors',
              mode === 'via-agent'
                ? 'bg-primary text-primary-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground'
            )}
          >
            Via Agent
          </button>
        </div>

        {/* Input */}
        {useFormMode ? (
          <ParamForm
            params={params}
            values={paramValues}
            onChange={(name, value) => setParamValues((prev) => ({ ...prev, [name]: value }))}
          />
        ) : (
          <div>
            <label className="text-xs font-medium mb-1 block">Arguments (JSON)</label>
            <textarea
              value={rawInput}
              onChange={(e) => setRawInput(e.target.value)}
              className="w-full font-mono text-xs p-2.5 border rounded-md resize-y min-h-[80px] bg-background focus:border-ring focus:ring-2 focus:ring-ring/50 outline-none transition-all"
              spellCheck={false}
              placeholder='{ "key": "value" }'
            />
          </div>
        )}

        {/* Submit Button */}
        <button
          type="button"
          onClick={() => { void handleSubmit(); }}
          disabled={isPending}
          className={cn(
            'inline-flex items-center justify-center gap-2 px-4 py-2 text-sm font-medium rounded-md transition-colors',
            'bg-primary text-primary-foreground hover:bg-primary/90',
            'disabled:opacity-50 disabled:pointer-events-none',
            'w-fit'
          )}
        >
          <Play size={14} />
          {isPending ? 'Running...' : 'Execute'}
        </button>

        {/* Error */}
        {hasError && (
          <div className="text-xs text-destructive p-3 bg-destructive/10 rounded-md border border-destructive/20">
            <span className="font-semibold">Error: </span>{errorMessage}
          </div>
        )}

        {/* Result */}
        {responseData != null && !hasError && (
          <div>
            <h4 className="text-xs font-medium uppercase tracking-wide text-muted-foreground mb-2">Result</h4>
            {typeof responseData === 'string' ? (
              <div className="text-sm p-3 bg-muted/50 rounded-md border">{responseData}</div>
            ) : (
              <JsonViewer data={responseData} defaultExpanded={true} maxInitialDepth={3} />
            )}
          </div>
        )}
      </CardContent>
    </Card>
  );
}
