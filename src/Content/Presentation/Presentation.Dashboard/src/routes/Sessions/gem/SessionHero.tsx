import { useMemo } from 'react';
import { Link } from 'react-router';
import { ContextBar } from '@/components/context/ContextBar';
import { ContextLegend } from '@/components/context/ContextLegend';
import { ScrubStrip, type ScrubTurn } from '@/components/context/ScrubStrip';
import { ContentsPanel } from './ContentsPanel';
import { CURRENT_TURN, type SessionGemState } from './useSessionGemState';
import type { ContextSnapshotEvent, LoadedItem } from '@/api/types';
import { cn } from '@/lib/utils';

interface SessionHeroProps {
  snapshots: ContextSnapshotEvent[];
  gem: SessionGemState;
  /** Context-window budget — drives bar headroom / over-budget styling. */
  budget?: number;
  /**
   * When set, renders a "View context inspector →" deep-link in the hero
   * header that navigates to the standalone /sessions/:id/context route.
   * SessionDetailPage passes its sessionId; embedded previews omit it.
   */
  sessionId?: string;
  className?: string;
}

/**
 * Composes the four Foresight hero primitives plus the contents panel into
 * the "gem" header that crowns the session page (foresight-dashboard-spec.md §4). State is
 * owned externally by {@link useSessionGemState}; this component is a thin
 * presentational shell so the same composition can later be lifted to the
 * design-system sandbox or to an embedded preview.
 */
export function SessionHero({
  snapshots,
  gem,
  budget,
  sessionId,
  className,
}: SessionHeroProps) {
  const scrubTurns = useMemo<ScrubTurn[]>(() => {
    return snapshots.map((s, i) => {
      const totalLoaded = s.loaded.reduce((sum, item) => sum + item.tokens, 0);
      const role = inferTurnRole(s.loaded);
      return {
        id: s.turnId,
        type: role,
        tokens: totalLoaded,
        label: `${role[0]!.toUpperCase()}${i + 1}`,
      };
    });
  }, [snapshots]);

  const activeScrubIndex = useMemo(() => {
    if (gem.activeTurnIndex === CURRENT_TURN) return scrubTurns.length - 1;
    return snapshots.findIndex((s) => s.turnIndex === gem.activeTurnIndex);
  }, [gem.activeTurnIndex, scrubTurns.length, snapshots]);

  const heroLabel =
    gem.activeTurnIndex === CURRENT_TURN || !gem.isScrubbed
      ? 'Context window — current'
      : `Context window — at ${snapshots[activeScrubIndex]?.turnId ?? 'turn'}`;

  const handleScrub = (index: number) => {
    const target = snapshots[index];
    if (!target) return;
    gem.setActiveTurnIndex(target.turnIndex);
  };

  return (
    <section
      data-testid="session-hero"
      data-scrubbed={gem.isScrubbed || undefined}
      className={cn(
        'rounded-xl border border-border bg-card p-5 space-y-4',
        className,
      )}
    >
      <header className="flex items-center justify-between gap-3">
        <h2 className="text-sm font-semibold uppercase tracking-wider text-muted-foreground">
          {heroLabel}
        </h2>
        <div className="flex items-center gap-2">
          {sessionId && (
            <Link
              data-testid="session-hero-inspector-link"
              to={`/sessions/${sessionId}/context`}
              className="text-[11px] font-medium uppercase tracking-wider text-cat-accent hover:underline"
            >
              Inspector →
            </Link>
          )}
          {gem.isScrubbed && (
            <button
              type="button"
              data-testid="session-hero-jump-current"
              onClick={gem.jumpToCurrent}
              className="rounded-full border border-cat-accent/40 bg-cat-accent/10 px-3 py-1 text-[11px] font-medium uppercase tracking-wider text-cat-accent hover:bg-cat-accent/20"
            >
              Jump to current ↓
            </button>
          )}
        </div>
      </header>

      {gem.displayedBreakdown ? (
        <ContextBar
          breakdown={gem.displayedBreakdown}
          budget={budget}
          size="lg"
          activeCategory={gem.activeCategory}
          onSegmentClick={(cat) =>
            gem.setActiveCategory(gem.activeCategory === cat ? null : cat)
          }
        />
      ) : (
        <div
          data-testid="session-hero-no-breakdown"
          className="h-6 w-full rounded bg-muted"
          aria-label="Context window — no data yet"
        />
      )}

      {gem.displayedBreakdown && (
        <ContextLegend
          breakdown={gem.displayedBreakdown}
          activeCategory={gem.activeCategory}
          onSelect={gem.setActiveCategory}
        />
      )}

      {scrubTurns.length > 1 && (
        <ScrubStrip
          turns={scrubTurns}
          activeIndex={activeScrubIndex}
          onScrub={handleScrub}
          showSparkline
        />
      )}

      <ContentsPanel
        snapshots={snapshots}
        activeTurnIndex={gem.activeTurnIndex}
        activeCategory={gem.activeCategory}
        onItemClick={(item: LoadedItem, turnIndex: number) =>
          gem.openDrawer(item, 'hero', turnIndex)
        }
      />
    </section>
  );
}

/**
 * Infer the role of a turn for the scrub strip from its loaded items. Prefer
 * the assistant role if any assistant content is present, then user, then
 * fall back to tool. Used for dot colouring only — does not affect routing.
 */
function inferTurnRole(loaded: LoadedItem[]): ScrubTurn['type'] {
  let hasAssistant = false;
  let hasUser = false;
  for (const item of loaded) {
    if (item.cat !== 'messages') continue;
    const what = item.what.toLowerCase();
    if (what.includes('assistant')) hasAssistant = true;
    else if (what.includes('user')) hasUser = true;
  }
  if (hasAssistant) return 'assistant';
  if (hasUser) return 'user';
  return 'tool';
}
