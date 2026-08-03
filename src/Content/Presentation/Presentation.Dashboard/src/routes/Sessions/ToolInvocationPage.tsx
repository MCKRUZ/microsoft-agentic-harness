import { Link, useParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { fetchToolInvocation } from '@/api/sessions';
import { PageHeader } from '@/components/primitives/PageHeader';
import { PanelCard } from '@/components/panels/PanelCard';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { EmptyState } from '@/components/panels/EmptyState';
import { Pill } from '@/components/primitives/Pill';

/**
 * Per-invocation deep-link. Renders a single tool execution's metadata, the
 * LLM-supplied arguments, and the stdout payload that was returned to the
 * model. Data shape mirrors `ToolExecutionRecord` from
 * `GET /api/sessions/:id/tools/:invocationId`.
 *
 * Args and stdout are rendered inside a `<pre>` so JSON / multi-line tool
 * output preserves whitespace. React already escapes string children, so
 * the untrusted LLM-supplied content cannot inject markup.
 */
export default function ToolInvocationPage() {
  const { sessionId, invocationId } = useParams<{
    sessionId: string;
    invocationId: string;
  }>();

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['tool-invocation', sessionId, invocationId],
    queryFn: () => fetchToolInvocation(sessionId!, invocationId!),
    enabled: !!sessionId && !!invocationId,
  });

  if (isLoading) {
    return (
      <div className="space-y-6">
        <PageHeader title="Tool invocation" subtitle={invocationId ?? ''} />
        <LoadingSkeleton />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="space-y-6">
        <PageHeader title="Tool invocation" subtitle={invocationId ?? ''} />
        <EmptyState
          title="Failed to load invocation"
          description={String(error ?? 'Unknown error')}
        />
        <Link
          to={`/sessions/${sessionId}`}
          className="text-cat-accent underline text-sm"
        >
          ← Back to session
        </Link>
      </div>
    );
  }

  if (!data) {
    return (
      <div className="space-y-6">
        <PageHeader title="Tool invocation" subtitle={invocationId ?? ''} />
        <EmptyState
          title="Invocation not found"
          description="The tool invocation may belong to a different session or have been deleted."
        />
        <Link
          to={`/sessions/${sessionId}`}
          className="text-cat-accent underline text-sm"
        >
          ← Back to session
        </Link>
      </div>
    );
  }

  const statusVariant: 'positive' | 'warning' | 'negative' =
    data.status === 'success'
      ? 'positive'
      : data.status === 'timeout'
        ? 'warning'
        : 'negative';

  return (
    <div className="space-y-6">
      <PageHeader
        title={data.toolName}
        subtitle={`${data.toolSource ?? 'unknown source'} · ${new Date(
          data.createdAt,
        ).toLocaleString()}`}
      />

      <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
        <div className="rounded border border-border p-3">
          <div className="text-xs text-muted-foreground">Status</div>
          <div className="mt-1.5">
            <Pill variant={statusVariant}>{data.status}</Pill>
          </div>
        </div>
        <div className="rounded border border-border p-3">
          <div className="text-xs text-muted-foreground">Duration</div>
          <div className="text-lg font-mono tabular-nums mt-0.5">
            {data.durationMs !== null ? `${data.durationMs}ms` : '—'}
          </div>
        </div>
        <div className="rounded border border-border p-3">
          <div className="text-xs text-muted-foreground">Result size</div>
          <div className="text-lg font-mono tabular-nums mt-0.5">
            {data.resultSize !== null ? data.resultSize : '—'}
          </div>
        </div>
        <div className="rounded border border-border p-3">
          <div className="text-xs text-muted-foreground">Call id</div>
          <div
            className="text-sm font-mono mt-0.5 truncate"
            title={data.callId ?? ''}
          >
            {data.callId ?? '—'}
          </div>
        </div>
      </div>

      {data.errorType && (
        <div className="rounded border border-otel-negative/40 bg-otel-negative/10 p-3 text-sm">
          <div className="font-semibold text-otel-negative mb-1">Error type</div>
          <div className="font-mono text-xs">{data.errorType}</div>
        </div>
      )}

      <PanelCard
        title="Arguments"
        description="JSON passed by the LLM to the tool dispatcher"
      >
        {data.args ? (
          <pre
            data-testid="tool-invocation-args"
            className="text-xs font-mono whitespace-pre-wrap break-words max-h-[400px] overflow-auto bg-muted/30 rounded p-3"
          >
            {data.args}
          </pre>
        ) : (
          <p className="text-xs text-muted-foreground py-6 text-center">
            No arguments captured
          </p>
        )}
      </PanelCard>

      <PanelCard
        title="Stdout"
        description="Result payload returned to the LLM"
      >
        {data.stdout ? (
          <pre
            data-testid="tool-invocation-stdout"
            className="text-xs font-mono whitespace-pre-wrap break-words max-h-[600px] overflow-auto bg-muted/30 rounded p-3"
          >
            {data.stdout}
          </pre>
        ) : (
          <p className="text-xs text-muted-foreground py-6 text-center">
            No stdout captured
          </p>
        )}
      </PanelCard>

      <Link
        to={`/sessions/${sessionId}`}
        className="text-cat-accent underline text-sm inline-block"
      >
        ← Back to session
      </Link>
    </div>
  );
}
