import { apiClient } from './client';
import { useSessionSnapshotsStore } from '@/stores/sessionSnapshotsStore';
import type { SessionRecord, SessionDetail } from './types';

export async function fetchSessions(
  limit = 50,
  offset = 0,
  status?: string,
  since?: string,
  until?: string,
): Promise<SessionRecord[]> {
  const params: Record<string, string | number> = { limit, offset };
  if (status) params['status'] = status;
  if (since) params['since'] = since;
  if (until) params['until'] = until;
  const { data } = await apiClient.get<SessionRecord[]>('/api/sessions', { params });
  return data;
}

export async function fetchSessionDetail(id: string): Promise<SessionDetail> {
  const { data } = await apiClient.get<SessionDetail>(`/api/sessions/${id}`);

  // PR 3: hydrate the per-session snapshot buffer from the persisted timeline
  // so a page refresh during a live conversation replays the Foresight
  // context-window timeline. Subsequent SignalR `ContextSnapshot` events
  // append on top via the same store. Hydrate keyed by conversationId — the
  // SignalR event payloads use the same key.
  if (data.session?.conversationId && data.snapshots) {
    useSessionSnapshotsStore
      .getState()
      .hydrateSession(data.session.conversationId, data.snapshots);
  }

  return data;
}
