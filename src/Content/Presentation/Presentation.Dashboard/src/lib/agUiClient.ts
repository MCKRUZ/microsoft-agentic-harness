import { HttpAgent } from '@ag-ui/client';
import { apiClient } from '@/api/client';
import { loginRequest, msalInstance } from '@/auth/authConfig';
import { IS_AUTH_DISABLED } from '@/auth/devAuth';

/** Path of the AG-UI streaming run endpoint (proxied to the AgentHub backend in dev). */
const AG_UI_RUN_URL = '/ag-ui/run';

/**
 * Resolves the bearer token for the AG-UI endpoint, mirroring the axios request interceptor
 * in {@link apiClient}. Returns an empty string when auth is disabled (dev mode) or no account
 * is signed in, in which case no Authorization header is sent and the backend's dev auth applies.
 */
export async function getAccessToken(): Promise<string> {
  if (IS_AUTH_DISABLED) return '';

  const account = msalInstance.getAllAccounts()[0];
  if (!account) return '';

  const result = await msalInstance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
  return result.accessToken;
}

/**
 * Builds the request headers for the AG-UI agent. Tokens expire, so callers should construct a
 * fresh agent (and thus fresh headers) per run rather than caching one long-term.
 */
async function buildAgUiHeaders(): Promise<Record<string, string>> {
  try {
    const token = await getAccessToken();
    if (token) return { Authorization: `Bearer ${token}` };
  } catch {
    // Token acquisition failed (e.g. auth disabled mid-session) — fall through to no header.
  }
  return {};
}

/**
 * Creates an {@link HttpAgent} pointed at the AG-UI run endpoint with a freshly-resolved auth
 * header. Use `agent.run(input)` to obtain the raw event observable for a run.
 */
export async function createAuthenticatedAgUiAgent(): Promise<HttpAgent> {
  const headers = await buildAgUiHeaders();
  return new HttpAgent({ url: AG_UI_RUN_URL, headers });
}

/**
 * Completes a mid-run client round-trip by posting the browser's result for a tool call back to the
 * backend, which unblocks the awaiting server-side tool and resumes the same run. Auth is applied by
 * the shared {@link apiClient} interceptor.
 */
export async function postToolResult(
  threadId: string,
  callId: string,
  result: string,
): Promise<void> {
  await apiClient.post('/ag-ui/tool-result', { threadId, callId, result });
}

/** Result of creating a conversation: its thread id and the agent it ended up bound to. */
export interface CreatedConversation {
  threadId: string;
  agentName: string;
}

/**
 * Creates a new conversation owned by the caller. The panel calls this once per chat session
 * before starting the first run.
 *
 * Pass `firstMessage` (and omit `agentName`) to opt into auto-routing — the backend picks an
 * agent from the message text, falling back to the default agent whenever it isn't confident.
 * The default (no options) preserves today's fixed-agent behavior.
 */
export async function createConversation(
  options: { agentName?: string; firstMessage?: string } = {},
): Promise<CreatedConversation> {
  const { agentName = options.firstMessage ? undefined : 'dashboard-agent', firstMessage } = options;
  const { data } = await apiClient.post<{ threadId: string; agentName: string }>(
    '/api/conversations',
    { agentName, firstMessage },
  );
  return { threadId: data.threadId, agentName: data.agentName };
}
