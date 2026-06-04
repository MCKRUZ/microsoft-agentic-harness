import type { AgentSummary, SessionRecord } from '@/api/types';
import { CATEGORY_ORDER, type CategoryKey } from '@/lib/categories';

/**
 * Enriched agent shape consumed by the AgentRail. Joins canonical registry
 * data (id / name / role) with derived rollup numbers (session count, most
 * recent activity) and visual chrome (color / initials) that the rail
 * decides client-side.
 */
export interface AgentRollup {
  id: string;
  name: string;
  role: string;
  sessionCount: number;
  /** ISO timestamp of the most recent session for this agent, or null. */
  lastActivity: string | null;
  /** Category color used to tint the avatar swatch. Deterministic per name. */
  color: CategoryKey;
  /** Two-character avatar text. Falls back to one letter for single-word names. */
  initials: string;
}

/**
 * Builds the agent rail's display roster by joining the registry agents
 * (from `/api/agents`) with the current sessions list. Every registry agent
 * gets a tile even when it has zero sessions today &mdash; we want the rail
 * to mirror the full available roster, not just who happens to be active in
 * the time window.
 *
 * When the registry is empty (older deploys, no AGENT.md manifests), the
 * function falls back to synthesising the roster from the sessions list so
 * the rail still renders something useful.
 *
 * Pure function; safe to call inside `useMemo`.
 */
export function buildAgentRoster(
  agents: AgentSummary[],
  sessions: SessionRecord[],
): AgentRollup[] {
  const byKey = indexSessionsByAgentKey(sessions);

  if (agents.length > 0) {
    // Dedupe by display name: two distinct registry ids that share the
    // same name would otherwise produce two tiles pointing at the same
    // session set (filter joins on agentName), and inflate the All-tile
    // totalSessions. Keep the first occurrence and drop the rest.
    const seenNames = new Set<string>();
    const deduped: AgentSummary[] = [];
    for (const a of agents) {
      if (seenNames.has(a.name)) continue;
      seenNames.add(a.name);
      deduped.push(a);
    }
    // Sessions can be stored with the registry display name ("Default Agent")
    // OR the slug/id ("default") depending on which path created them. Look
    // both up so the tile session counts include all rows that belong to this
    // agent regardless of which form was persisted.
    return deduped.map((a) => {
      const fromName = byKey.get(normalizeAgentKey(a.name)) ?? [];
      const fromId = byKey.get(normalizeAgentKey(a.id)) ?? [];
      const merged = mergeUnique(fromName, fromId);
      return makeRollupFromRegistry(a, merged);
    });
  }

  // Fallback path: registry is empty. Synthesise from sessions so the user
  // still sees the agents that exist in the data, just without descriptions.
  // Group by the raw agentName so the fallback tile labels match what's on
  // the rows verbatim.
  const byRawName = new Map<string, SessionRecord[]>();
  for (const s of sessions) {
    const lane = byRawName.get(s.agentName) ?? [];
    lane.push(s);
    byRawName.set(s.agentName, lane);
  }
  return Array.from(byRawName.entries())
    .map(([name, rows]) => makeRollupFromSessions(name, rows))
    // Stable order so the fallback rail doesn't reshuffle on every refetch.
    .sort((a, b) => a.name.localeCompare(b.name));
}

/** Normalises an agent identifier for join: lowercase, trimmed, spaces collapsed. */
function normalizeAgentKey(value: string): string {
  return value.trim().toLowerCase().replace(/\s+/g, ' ');
}

function indexSessionsByAgentKey(
  sessions: SessionRecord[],
): Map<string, SessionRecord[]> {
  const byKey = new Map<string, SessionRecord[]>();
  for (const s of sessions) {
    const key = normalizeAgentKey(s.agentName);
    const lane = byKey.get(key) ?? [];
    lane.push(s);
    byKey.set(key, lane);
  }
  return byKey;
}

function mergeUnique(a: SessionRecord[], b: SessionRecord[]): SessionRecord[] {
  if (a.length === 0) return b;
  if (b.length === 0) return a;
  const seen = new Set<string>(a.map((s) => s.id));
  const out = [...a];
  for (const s of b) {
    if (seen.has(s.id)) continue;
    seen.add(s.id);
    out.push(s);
  }
  return out;
}

function makeRollupFromRegistry(
  agent: AgentSummary,
  sessions: SessionRecord[],
): AgentRollup {
  return {
    id: agent.id,
    name: agent.name,
    role: agent.description ?? '',
    sessionCount: sessions.length,
    lastActivity: mostRecentStartedAt(sessions),
    color: hashToCategory(agent.name),
    initials: deriveInitials(agent.name),
  };
}

function makeRollupFromSessions(
  name: string,
  sessions: SessionRecord[],
): AgentRollup {
  return {
    id: name,
    name,
    role: '',
    sessionCount: sessions.length,
    lastActivity: mostRecentStartedAt(sessions),
    color: hashToCategory(name),
    initials: deriveInitials(name),
  };
}

function mostRecentStartedAt(sessions: SessionRecord[]): string | null {
  if (sessions.length === 0) return null;
  // Sessions arrive newest-first from the controller but we don't rely on that
  // ordering — pick the max explicitly so refetches and re-sorts don't shift
  // recency under us.
  let best: string | null = null;
  for (const s of sessions) {
    if (best === null || s.startedAt > best) best = s.startedAt;
  }
  return best;
}

/**
 * Deterministic FNV-1a hash → CATEGORY_ORDER index so every agent gets a
 * stable colour without backend support. Same name across renders → same
 * color across renders.
 */
export function hashToCategory(name: string): CategoryKey {
  let hash = 0x811c9dc5; // FNV-1a 32-bit offset basis
  for (let i = 0; i < name.length; i++) {
    hash ^= name.charCodeAt(i);
    hash = (hash + ((hash << 1) + (hash << 4) + (hash << 7) + (hash << 8) + (hash << 24))) >>> 0;
  }
  return CATEGORY_ORDER[hash % CATEGORY_ORDER.length]!;
}

/**
 * Two-character avatar text. Uppercases the first letter of the first two
 * whitespace-separated tokens. Single-token names get a one-character
 * fallback to avoid awkward "C·" cases for camelCase names.
 *
 * `"Code Assistant"` → `"CA"`
 * `"ResearchAgent"` → `"R"`
 * `""` → `"?"`
 */
export function deriveInitials(name: string): string {
  const trimmed = name.trim();
  if (trimmed.length === 0) return '?';
  const tokens = trimmed.split(/\s+/);
  if (tokens.length >= 2) {
    return (tokens[0]!.charAt(0) + tokens[1]!.charAt(0)).toUpperCase();
  }
  return trimmed.charAt(0).toUpperCase();
}
