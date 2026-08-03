import { useMemo } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { fetchSessionDetail } from '@/api/sessions';
import { useSessionSnapshots } from '@/stores/sessionSnapshotsStore';
import { PanelCard } from '@/components/panels/PanelCard';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { EmptyState } from '@/components/panels/EmptyState';
import { ContextDrawer } from '@/components/context/ContextDrawer';
import { CategorySwatch } from '@/components/context/CategorySwatch';
import {
  CATEGORY_ORDER,
  CATEGORY_LABEL,
  CATEGORY_DESCRIPTION,
  type CategoryKey,
} from '@/lib/categories';
import { aggregateLoadedItems, totalTokensInCategory } from '@/lib/loadedItems';
import { useSessionGemState } from './gem/useSessionGemState';
import { useResolvedDrawerBody } from './gem/useResolvedDrawerBody';
import type { LoadedItem } from '@/api/types';

/**
 * Standalone Context Inspector — foresight-dashboard-spec.md §9.3. Renders every loaded
 * artifact across the whole session, grouped into six category lanes.
 * Click any row → ContextDrawer opens with the (currently truncated)
 * preview body resolved by `buildDrawerContent` per category.
 *
 * Reuses the gem state hook from PR 4 so the drawer + walk-within-category
 * semantics match SessionDetailPage exactly. The page is a pure consumer
 * of `useSessionSnapshots(conversationId)` + the session detail query;
 * no new backend, no new wire shape.
 */
export default function ContextInspectorPage() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const navigate = useNavigate();

  const { data, isLoading, isError } = useQuery({
    queryKey: ['session-detail', sessionId],
    queryFn: () => fetchSessionDetail(sessionId!),
    enabled: !!sessionId,
  });

  const conversationId = data?.session.conversationId;
  const snapshots = useSessionSnapshots(conversationId);

  const gem = useSessionGemState({
    snapshots,
    fallbackBreakdown: data?.breakdown ?? null,
    messages: data?.messages ?? [],
    tools: data?.tools ?? [],
  });

  // Resolve the open drawer item's full body (composed system prompt, skill
  // instructions, tool/MCP schema, sub-agent description) via the shared hook —
  // the same resolution SessionDetailPage uses, so both surfaces behave
  // identically instead of this page being stuck on the "Loading…" placeholder.
  const drawerBody = useResolvedDrawerBody(sessionId, gem.drawerItem);

  const lanes = useMemo(() => aggregateLoadedItems(snapshots), [snapshots]);
  // Maintain a parallel { item, turnIndex } map so each row knows which
  // snapshot it came from — same trick ContentsPanel uses.
  const rowsByCategory = useMemo(() => {
    // The empty arrays have to be typed at the point they are created. Left bare, they infer
    // as never[], and Object.fromEntries then yields { [k: string]: never[] } — a type with
    // no overlap with the target, which is why the assertion was rejected outright rather
    // than merely being unsound.
    type CategoryRows = { item: LoadedItem; turnIndex: number }[];
    const result = Object.fromEntries(
      CATEGORY_ORDER.map((k) => [k, [] as CategoryRows]),
    ) as Record<CategoryKey, CategoryRows>;
    for (const snap of snapshots) {
      for (const item of snap.loaded) {
        result[item.cat].push({ item, turnIndex: snap.turnIndex });
      }
    }
    return result;
  }, [snapshots]);

  if (isLoading) {
    return (
      <div className="space-y-6">
        <BackButton onClick={() => navigate(`/sessions/${sessionId}`)} />
        <LoadingSkeleton />
      </div>
    );
  }

  if (isError || !data) {
    return (
      <div className="space-y-6">
        <BackButton onClick={() => navigate('/sessions')} />
        <PanelCard title="Session Not Found">
          <EmptyState
            title="Unable to load session context"
            description="This session may not exist or its snapshot store is unavailable."
          />
        </PanelCard>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <BackButton onClick={() => navigate(`/sessions/${sessionId}`)} />

      <header className="rounded-xl border border-border bg-card p-5">
        <h1 className="text-lg font-bold text-card-foreground">
          Context Inspector
        </h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Every artifact loaded into the model's context window for{' '}
          <span className="font-mono">{data.session.agentName}</span> · session{' '}
          <span className="font-mono">{sessionId}</span>
        </p>
      </header>

      <div
        data-testid="context-inspector-grid"
        className="grid grid-cols-1 gap-4 lg:grid-cols-2 xl:grid-cols-3"
      >
        {CATEGORY_ORDER.map((cat) => (
          <CategoryLane
            key={cat}
            category={cat}
            rows={rowsByCategory[cat]}
            laneTotal={totalTokensInCategory(lanes[cat])}
            onItemClick={(item, turnIndex) =>
              gem.openDrawer(item, 'hero', turnIndex)
            }
          />
        ))}
      </div>

      <ContextDrawer
        open={gem.drawerItem !== null}
        onOpenChange={(open) => {
          if (!open) gem.closeDrawer();
        }}
        category={gem.drawerItem?.item.cat ?? 'system'}
        name={gem.drawerItem?.name ?? ''}
        path={gem.drawerItem?.path ?? ''}
        role={gem.drawerItem?.content.role}
        body={drawerBody}
        lang={gem.drawerItem?.content.lang}
        onPrev={() => gem.walkDrawer(-1)}
        onNext={() => gem.walkDrawer(1)}
      />
    </div>
  );
}

interface CategoryLaneProps {
  category: CategoryKey;
  rows: { item: LoadedItem; turnIndex: number }[];
  laneTotal: number;
  onItemClick: (item: LoadedItem, turnIndex: number) => void;
}

function CategoryLane({
  category,
  rows,
  laneTotal,
  onItemClick,
}: CategoryLaneProps) {
  const sorted = useMemo(
    () => [...rows].sort((a, b) => b.item.tokens - a.item.tokens),
    [rows],
  );

  return (
    <section
      data-testid={`inspector-lane-${category}`}
      data-category={category}
      className="rounded-md border border-border bg-card"
    >
      <header className="flex items-center justify-between gap-2 border-b border-border px-3 py-2">
        <div className="flex items-center gap-2">
          <CategorySwatch category={category} size="xs" />
          <h2 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
            {CATEGORY_LABEL[category]}
          </h2>
        </div>
        <span
          className="font-mono text-xs tabular-nums text-muted-foreground"
          title={CATEGORY_DESCRIPTION[category]}
        >
          {laneTotal.toLocaleString()}
        </span>
      </header>
      {sorted.length === 0 ? (
        <p
          data-testid={`inspector-lane-${category}-empty`}
          className="px-3 py-4 text-center text-xs italic text-muted-foreground/80"
        >
          Nothing {CATEGORY_LABEL[category].toLowerCase()} in this session.
        </p>
      ) : (
        <ul className="divide-y divide-border">
          {sorted.map((row, idx) => {
            const { item, turnIndex } = row;
            const pct = laneTotal > 0 ? (item.tokens / laneTotal) * 100 : 0;
            return (
              <li key={`${category}-${idx}-${item.what}`}>
                <button
                  type="button"
                  data-testid={`inspector-row-${category}-${idx}`}
                  onClick={() => onItemClick(item, turnIndex)}
                  className="flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-accent/40 focus-visible:bg-accent/60 focus-visible:outline-none"
                >
                  <span className="min-w-0 flex-1 truncate text-sm text-foreground">
                    {item.what}
                    {item.ref && (
                      <span className="ml-2 font-mono text-[11px] text-muted-foreground">
                        · {item.ref}
                      </span>
                    )}
                  </span>
                  <span className="font-mono text-xs tabular-nums text-muted-foreground">
                    {item.tokens.toLocaleString()}
                  </span>
                  <span className="w-12 text-right font-mono text-[11px] tabular-nums text-muted-foreground/80">
                    {pct.toFixed(1)}%
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

function BackButton({ onClick }: { onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      className="flex items-center gap-1.5 text-sm text-otel-accent hover:text-otel-accent-dim transition-colors"
    >
      ← back
    </button>
  );
}
