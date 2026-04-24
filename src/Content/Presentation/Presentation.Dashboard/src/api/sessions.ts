import { apiClient } from './client';
import type { SessionRecord, SessionDetail } from './types';

export async function fetchSessions(limit = 50, offset = 0, status?: string): Promise<SessionRecord[]> {
  const params: Record<string, string | number> = { limit, offset };
  if (status) params['status'] = status;
  const { data } = await apiClient.get<SessionRecord[]>('/api/sessions', { params });
  return data;
}

export async function fetchSessionDetail(id: string): Promise<SessionDetail> {
  const { data } = await apiClient.get<SessionDetail>(`/api/sessions/${id}`);
  return data;
}
