import type { SessionMessageRecord, ToolExecutionRecord } from '@/api/types';
import { RoleBadge } from './RoleBadge';
import { formatTokens, formatCost } from './format';

interface ConversationTimelineProps {
  messages: SessionMessageRecord[];
  toolExecutions: ToolExecutionRecord[];
}

export function ConversationTimeline({ messages, toolExecutions }: ConversationTimelineProps) {
  const toolsByMessageId = new Map<string, ToolExecutionRecord[]>();
  for (const t of toolExecutions) {
    if (!t.messageId) continue;
    const existing = toolsByMessageId.get(t.messageId) ?? [];
    existing.push(t);
    toolsByMessageId.set(t.messageId, existing);
  }

  const sorted = [...messages].sort((a, b) => a.turnIndex - b.turnIndex || a.createdAt.localeCompare(b.createdAt));

  return (
    <div className="space-y-1">
      {sorted.map((msg) => {
        const msgTools = toolsByMessageId.get(msg.id) ?? [];
        return (
          <MessageRow key={msg.id} message={msg} tools={msgTools} />
        );
      })}
    </div>
  );
}

interface MessageRowProps {
  message: SessionMessageRecord;
  tools: ToolExecutionRecord[];
}

function MessageRow({ message, tools }: MessageRowProps) {
  const hasTokens = message.inputTokens > 0 || message.outputTokens > 0;
  return (
    <div className="group">
      <div className="flex items-start gap-3 py-2.5 px-3 rounded-lg hover:bg-muted/30 transition-colors">
        <div className="flex-shrink-0 w-8 text-right">
          <span className="text-xs font-mono text-muted-foreground">#{message.turnIndex}</span>
        </div>

        <div className="flex-shrink-0 w-20">
          <RoleBadge role={message.role} />
        </div>

        <div className="flex-1 min-w-0">
          <p className="text-sm text-card-foreground truncate">
            {message.contentPreview ?? <span className="italic text-muted-foreground">No content</span>}
          </p>
          {message.toolNames && message.toolNames.length > 0 && (
            <div className="flex flex-wrap gap-1 mt-1">
              {message.toolNames.map((name) => (
                <span key={name} className="text-[10px] bg-purple-500/10 text-purple-400 px-1.5 py-0.5 rounded">
                  {name}
                </span>
              ))}
            </div>
          )}
        </div>

        {hasTokens && (
          <div className="flex-shrink-0 text-right text-xs text-muted-foreground whitespace-nowrap">
            <span>{formatTokens(message.inputTokens)} in</span>
            <span className="mx-1">/</span>
            <span>{formatTokens(message.outputTokens)} out</span>
            {message.cacheRead > 0 && (
              <>
                <span className="mx-1">/</span>
                <span className="text-amber-400">{formatTokens(message.cacheRead)} cache</span>
              </>
            )}
          </div>
        )}

        <div className="flex-shrink-0 w-16 text-right text-xs text-muted-foreground">
          {message.costUsd > 0 ? formatCost(message.costUsd) : ''}
        </div>
      </div>

      {tools.length > 0 && (
        <div className="ml-14 pl-3 border-l-2 border-purple-500/20 space-y-0.5 mb-1">
          {tools.map((t) => (
            <ToolCallRow key={t.id} tool={t} />
          ))}
        </div>
      )}
    </div>
  );
}

function ToolCallRow({ tool }: { tool: ToolExecutionRecord }) {
  const isError = tool.status.toLowerCase() === 'error';
  return (
    <div className="flex items-center gap-3 py-1 px-2 text-xs">
      <span className="font-mono text-purple-400">{tool.toolName}</span>
      {tool.toolSource && (
        <span className="text-muted-foreground">({tool.toolSource})</span>
      )}
      {tool.durationMs !== null && (
        <span className="text-muted-foreground">{tool.durationMs}ms</span>
      )}
      {isError ? (
        <span className="text-red-400">{tool.errorType ?? 'error'}</span>
      ) : (
        <span className="text-emerald-400">{tool.status}</span>
      )}
      {tool.resultSize !== null && (
        <span className="text-muted-foreground">{formatTokens(tool.resultSize)} chars</span>
      )}
    </div>
  );
}
