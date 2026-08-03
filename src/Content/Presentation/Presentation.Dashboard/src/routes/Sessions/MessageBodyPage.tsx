import { Link, useParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { fetchMessageBody } from '@/api/sessions';
import { PageHeader } from '@/components/primitives/PageHeader';
import { PanelCard } from '@/components/panels/PanelCard';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { EmptyState } from '@/components/panels/EmptyState';

/**
 * File-body deep-link. Renders the full content of a single session message
 * captured before the 500-char preview truncation. Body is from
 * `GET /api/sessions/:id/messages/:messageId`; server scopes the lookup
 * to the parent session id.
 *
 * Falls back gracefully when the row predates the `content_full` column
 * (the deep-link existed before the schema migration was applied) by
 * showing the preview with a note that the full body wasn't captured.
 */
export default function MessageBodyPage() {
  const { sessionId, messageId } = useParams<{
    sessionId: string;
    messageId: string;
  }>();

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['message-body', sessionId, messageId],
    queryFn: () => fetchMessageBody(sessionId!, messageId!),
    enabled: !!sessionId && !!messageId,
  });

  if (isLoading) {
    return (
      <div className="space-y-6">
        <PageHeader title="Message body" subtitle={messageId ?? ''} />
        <LoadingSkeleton />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="space-y-6">
        <PageHeader title="Message body" subtitle={messageId ?? ''} />
        <EmptyState
          title="Failed to load message"
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
        <PageHeader title="Message body" subtitle={messageId ?? ''} />
        <EmptyState
          title="Message not found"
          description="The message may belong to a different session or have been deleted."
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

  const usingFallback = data.contentFull === null && data.contentPreview !== null;

  return (
    <div className="space-y-6">
      <PageHeader
        title={`Turn ${data.turnIndex} · ${data.role}`}
        subtitle={`${data.source ?? 'unknown source'} · ${new Date(
          data.createdAt,
        ).toLocaleString()}`}
      />

      {usingFallback && (
        <div className="rounded border border-otel-warning/40 bg-otel-warning/10 p-3 text-xs text-otel-warning">
          This message predates the <code>content_full</code> column. Showing
          the 500-char preview only.
        </div>
      )}

      <PanelCard
        title="Body"
        description={data.model ? `model: ${data.model}` : undefined}
      >
        {data.contentFull || data.contentPreview ? (
          <pre
            data-testid="message-body-content"
            className="text-sm font-mono whitespace-pre-wrap break-words max-h-[80vh] overflow-auto bg-muted/30 rounded p-3"
          >
            {data.contentFull ?? data.contentPreview ?? ''}
          </pre>
        ) : (
          <p className="text-xs text-muted-foreground py-6 text-center">
            No content captured
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
