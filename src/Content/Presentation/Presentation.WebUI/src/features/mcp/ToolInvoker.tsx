import { useState } from 'react';
import { useAgentHub } from '@/hooks/useAgentHub';
import { useChatStore } from '@/stores/chatStore';
import { useInvokeTool, type McpTool } from './useMcpQuery';

interface ToolInvokerProps {
  tool: McpTool;
}

type Mode = 'direct' | 'via-agent';

function formatResult(data: unknown): string {
  try {
    return JSON.stringify(data, null, 2);
  } catch {
    return String(data);
  }
}

export function ToolInvoker({ tool }: ToolInvokerProps) {
  const [mode, setMode] = useState<Mode>('direct');
  const [input, setInput] = useState('{}');
  const [agentResponse, setAgentResponse] = useState<string | null>(null);
  const [agentError, setAgentError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  const conversationId = useChatStore((s) => s.conversationId);
  const { invokeToolViaAgent } = useAgentHub();
  const mutation = useInvokeTool();

  const handleSubmit = async () => {
    let args: Record<string, unknown>;
    try {
      args = JSON.parse(input) as Record<string, unknown>;
    } catch {
      setAgentError('Invalid JSON input');
      return;
    }

    if (mode === 'direct') {
      mutation.mutate({ name: tool.name, args });
    } else {
      if (!conversationId) {
        setAgentError('No active conversation. Send a message in chat first.');
        return;
      }
      setAgentResponse(null);
      setAgentError(null);
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

  return (
    <div className="flex flex-col gap-2 mt-2">
      <div className="flex gap-1">
        <button
          type="button"
          onClick={() => { setMode('direct'); }}
          className={`px-3 py-1 text-xs rounded border ${mode === 'direct' ? 'bg-primary text-primary-foreground' : 'hover:bg-accent'}`}
        >
          Direct
        </button>
        <button
          type="button"
          onClick={() => { setMode('via-agent'); }}
          className={`px-3 py-1 text-xs rounded border ${mode === 'via-agent' ? 'bg-primary text-primary-foreground' : 'hover:bg-accent'}`}
        >
          Via Agent
        </button>
      </div>

      <textarea
        value={input}
        onChange={(e) => { setInput(e.target.value); }}
        className="font-mono text-xs p-2 border rounded resize-y min-h-[60px] bg-background"
        spellCheck={false}
      />

      <button
        type="button"
        onClick={() => { void handleSubmit(); }}
        disabled={mutation.isPending || isSubmitting}
        className="px-3 py-1 text-sm rounded bg-primary text-primary-foreground hover:bg-primary/90 disabled:opacity-50"
      >
        {mutation.isPending || isSubmitting ? 'Running…' : 'Submit'}
      </button>

      {hasError && (
        <div className="text-xs text-destructive p-2 bg-destructive/10 rounded">
          <span className="font-semibold">Error: </span>{errorMessage}
        </div>
      )}

      {responseData != null && !hasError && (
        <pre className="text-xs p-2 bg-muted rounded overflow-auto max-h-48 break-all whitespace-pre-wrap">
          {formatResult(responseData)}
        </pre>
      )}
    </div>
  );
}
