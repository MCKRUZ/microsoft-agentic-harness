import { useMemo } from 'react';
import { Link, useParams } from 'react-router';
import { cn } from '@/lib/utils';
import { ContextBar } from '@/components/context/ContextBar';
import { CategorySwatch } from '@/components/context/CategorySwatch';
import { type CategoryKey } from '@/lib/categories';
import type {
  ContextSnapshotEvent,
  LoadedItem,
  SessionMessageRecord,
} from '@/api/types';
import { CURRENT_TURN } from './useSessionGemState';

interface SessionTimelineProps {
  snapshots: ContextSnapshotEvent[];
  /** Full message list — joined by `turnIndex` to provide row body excerpts. */
  messages: SessionMessageRecord[];
  /** Active turn (or CURRENT_TURN); drives the accent halo + dimming. */
  activeTurnIndex: number;
  /** Currently filtered category, or null. Forwarded to per-turn ContextBar. */
  activeCategory: CategoryKey | null;
  /** Click handler for the turn header → scrub the hero to this turn. */
  onTurnScrub: (turnIndex: number) => void;
  /**
   * Click handler for one of a turn's loaded items → open the drawer.
   * Receives the turn this item belongs to so the drawer can resolve message
   * bodies without a reference-equality scan.
   */
  onLoadedClick: (item: LoadedItem, turnIndex: number) => void;
  /** Optional context-window budget for the per-turn mini-bars. */
  budget?: number;
  className?: string;
}

/**
 * Per-turn rail under the hero. Each row mirrors foresight-dashboard-spec.md §4: turn id +
 * role pill + time + cumulative tokens, a body excerpt, a small ContextBar
 * snapshot, and the per-turn `loaded[]` items. Clicking the turn header
 * rewinds the hero; clicking any loaded item opens the drawer.
 */
export function SessionTimeline({
  snapshots,
  messages,
  activeTurnIndex,
  activeCategory,
  onTurnScrub,
  onLoadedClick,
  budget,
  className,
}: SessionTimelineProps) {
  // sessionId comes from the parent /sessions/:sessionId route — needed to
  // build the per-message file-body deep-link.
  const { sessionId } = useParams<{ sessionId: string }>();

  const messagesByTurn = useMemo(() => {
    const map = new Map<number, SessionMessageRecord[]>();
    for (const m of messages) {
      const lane = map.get(m.turnIndex) ?? [];
      lane.push(m);
      map.set(m.turnIndex, lane);
    }
    return map;
  }, [messages]);

  if (snapshots.length === 0) {
    return (
      <p
        data-testid="session-timeline-empty"
        className={cn('text-sm italic text-muted-foreground', className)}
      >
        No turns yet. The timeline populates as the conversation progresses.
      </p>
    );
  }

  return (
    <ol
      data-testid="session-timeline"
      className={cn('space-y-3', className)}
    >
      {snapshots.map((snap) => {
        const turnMessages = messagesByTurn.get(snap.turnIndex) ?? [];
        const isActive =
          activeTurnIndex === snap.turnIndex ||
          (activeTurnIndex === CURRENT_TURN &&
            snap.turnIndex === snapshots[snapshots.length - 1]!.turnIndex);
        return (
          <TimelineRow
            key={snap.turnId}
            snapshot={snap}
            turnMessages={turnMessages}
            isActive={isActive}
            activeCategory={activeCategory}
            onTurnScrub={onTurnScrub}
            onLoadedClick={onLoadedClick}
            budget={budget}
            sessionId={sessionId}
          />
        );
      })}
    </ol>
  );
}

interface TimelineRowProps {
  snapshot: ContextSnapshotEvent;
  turnMessages: SessionMessageRecord[];
  isActive: boolean;
  activeCategory: CategoryKey | null;
  onTurnScrub: (turnIndex: number) => void;
  onLoadedClick: (item: LoadedItem, turnIndex: number) => void;
  budget?: number;
  sessionId: string | undefined;
}

function TimelineRow({
  snapshot,
  turnMessages,
  isActive,
  activeCategory,
  onTurnScrub,
  onLoadedClick,
  budget,
  sessionId,
}: TimelineRowProps) {
  // Heuristic role: prefer assistant message if present; else first turn message.
  const headerMessage =
    turnMessages.find((m) => m.role === 'assistant') ?? turnMessages[0];
  const role = (headerMessage?.role as 'user' | 'assistant' | 'tool') ?? 'user';

  const deltaTokens = useMemo(
    () => snapshot.loaded.reduce((sum, item) => sum + item.tokens, 0),
    [snapshot.loaded],
  );

  const time = useMemo(() => {
    try {
      return new Date(snapshot.capturedAtUtc).toLocaleTimeString();
    } catch {
      return snapshot.capturedAtUtc;
    }
  }, [snapshot.capturedAtUtc]);

  return (
    <li
      data-testid={`timeline-row-${snapshot.turnIndex}`}
      data-active={isActive || undefined}
      data-role={role}
      className={cn(
        'rounded-md border bg-card transition-colors',
        isActive
          ? 'border-cat-accent ring-1 ring-cat-accent/40'
          : 'border-border',
      )}
    >
      <header className="flex items-center justify-between gap-3 border-b border-border px-4 py-2">
        <button
          type="button"
          data-testid={`timeline-row-${snapshot.turnIndex}-scrub`}
          onClick={() => onTurnScrub(snapshot.turnIndex)}
          className="flex items-center gap-2 text-left text-sm focus-visible:outline-none focus-visible:underline"
        >
          <span
            data-testid={`timeline-row-${snapshot.turnIndex}-role`}
            data-role={role}
            className={cn(
              'inline-flex h-5 items-center rounded-full px-2 text-[10px] font-medium uppercase tracking-wider',
              role === 'user' && 'bg-cat-accent/15 text-cat-accent',
              role === 'assistant' && 'bg-cat-messages/15 text-cat-messages',
              role === 'tool' && 'bg-cat-tools/15 text-cat-tools',
            )}
          >
            {role}
          </span>
          <span className="font-mono text-xs tabular-nums text-muted-foreground">
            {snapshot.turnId}
          </span>
          <span className="font-mono text-[11px] tabular-nums text-muted-foreground/80">
            {time}
          </span>
        </button>
        <span className="font-mono text-xs tabular-nums text-muted-foreground">
          +{deltaTokens.toLocaleString()} ctx
        </span>
      </header>

      {headerMessage?.contentPreview && (
        <div className="px-4 py-2">
          <p
            data-testid={`timeline-row-${snapshot.turnIndex}-excerpt`}
            className="text-sm leading-relaxed text-foreground/90 line-clamp-3"
          >
            {headerMessage.contentPreview}
          </p>
          {sessionId && headerMessage.id && (
            <Link
              data-testid={`timeline-row-${snapshot.turnIndex}-view-full`}
              to={`/sessions/${sessionId}/files/${headerMessage.id}`}
              className="mt-1 inline-block text-[11px] text-cat-accent hover:underline"
            >
              view full →
            </Link>
          )}
        </div>
      )}

      <div className="px-4 pb-2">
        <ContextBar
          breakdown={snapshot.ctxAfter}
          budget={budget}
          size="md"
          activeCategory={activeCategory}
          ariaLabel={`Context window after ${snapshot.turnId}`}
        />
      </div>

      {snapshot.loaded.length > 0 && (
        <ul
          data-testid={`timeline-row-${snapshot.turnIndex}-loaded`}
          className="divide-y divide-border border-t border-border"
        >
          {snapshot.loaded.map((item, idx) => (
            <li key={`${snapshot.turnId}-loaded-${idx}-${item.what}`}>
              <button
                type="button"
                data-testid={`timeline-loaded-${snapshot.turnIndex}-${idx}`}
                onClick={() => onLoadedClick(item, snapshot.turnIndex)}
                className="flex w-full items-center gap-3 px-4 py-2 text-left text-xs hover:bg-accent/40 focus-visible:bg-accent/60 focus-visible:outline-none"
              >
                <CategorySwatch category={item.cat} size="xs" />
                <span className="min-w-0 flex-1 truncate text-foreground/90">
                  {item.what}
                  {item.ref && (
                    <span className="ml-2 font-mono text-[11px] text-muted-foreground">
                      · {item.ref}
                    </span>
                  )}
                </span>
                <span className="font-mono tabular-nums text-muted-foreground">
                  {item.tokens.toLocaleString()}
                </span>
                <span className="text-muted-foreground/60" aria-hidden="true">
                  →
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}
