import axios from 'axios';
import { apiClient } from '@/lib/apiClient';
import type { ChatMessage } from './useChatStore';
import type { ServerConversationMessage } from '@/hooks/useAgentHub';

/** Shape of the record returned by <c>GET /api/conversations/:id</c> (only the fields the UI reads). */
interface ConversationRecordResponse {
  messages?: ServerConversationMessage[] | null;
}

/**
 * Maps the server's persisted messages to the client {@link ChatMessage} shape, keeping only the
 * user/assistant turns the transcript renders. Widget and tool-call payloads pass straight through.
 * Shared shape with the SignalR <c>StartConversation</c> history contract, so both load paths agree.
 */
export function mapServerMessages(history: ServerConversationMessage[]): ChatMessage[] {
  return history
    .filter((m) => {
      const r = m.role.toLowerCase();
      return r === 'user' || r === 'assistant';
    })
    .map((m) => ({
      id: m.id,
      role: m.role.toLowerCase() as 'user' | 'assistant',
      content: m.content,
      timestamp: new Date(m.timestamp),
      toolCalls: m.toolCalls ?? undefined,
      widget: m.widget ?? undefined,
    }));
}

/**
 * Loads a conversation's persisted transcript for display <em>without ever creating it server-side</em>.
 *
 * Reads the pure query endpoint <c>GET /api/conversations/:id</c> and maps its stored messages. A 404
 * (the id has never been messaged — e.g. a freshly minted "New chat" id, or a blank <c>/chat</c> that
 * was refreshed) resolves to an empty transcript rather than an error. This replaces the old mount-time
 * hub <c>StartConversation</c> call, which created an empty conversation as a side effect of loading
 * history and was the source of the accumulating phantom conversations in the sidebar.
 */
export async function loadConversationHistory(conversationId: string): Promise<ChatMessage[]> {
  try {
    const res = await apiClient.get<ConversationRecordResponse>(
      `/api/conversations/${conversationId}`,
    );
    return mapServerMessages(res.data.messages ?? []);
  } catch (err) {
    // A not-yet-persisted conversation is the normal state for a brand-new chat, not a failure.
    if (axios.isAxiosError(err) && err.response?.status === 404) return [];
    throw err;
  }
}
