import { HttpAgent } from '@ag-ui/client';
import type { HttpAgentConfig } from '@ag-ui/client';

const AG_UI_BASE_URL = '/ag-ui/run';

/**
 * Creates an HttpAgent pointed at the AG-UI endpoint.
 *
 * Headers must be resolved before construction — tokens expire, so callers
 * should call `buildAgUiHeaders` and pass the result here each time they
 * start a new conversation run rather than caching the agent long-term.
 */
export function createAgUiAgent(headers: Record<string, string> = {}): HttpAgent {
  const config: HttpAgentConfig = {
    url: AG_UI_BASE_URL,
    headers,
  };
  return new HttpAgent(config);
}

/**
 * Resolves the Authorization header for the AG-UI endpoint.
 * Returns an empty object when auth is disabled (dev mode).
 */
export async function buildAgUiHeaders(
  getAccessToken: () => Promise<string>,
): Promise<Record<string, string>> {
  try {
    const token = await getAccessToken();
    if (token) {
      return { Authorization: `Bearer ${token}` };
    }
  } catch {
    // Auth disabled in dev — no token needed
  }
  return {};
}

/**
 * Convenience factory that resolves auth headers and returns a ready-to-use
 * HttpAgent. Use this as the primary entry point from UI components.
 */
export async function createAuthenticatedAgUiAgent(
  getAccessToken: () => Promise<string>,
): Promise<HttpAgent> {
  const headers = await buildAgUiHeaders(getAccessToken);
  return createAgUiAgent(headers);
}
