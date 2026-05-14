/**
 * Playwright global setup — triggers a real echo agent conversation before any
 * tests run. This seeds the backend with real session data, metrics, and tool
 * invocation records that the Dashboard should render.
 *
 * No synthetic data. No mocks. If this step produces nothing, the tests
 * correctly fail because the pipeline is broken.
 */

const API_URL = process.env.API_URL ?? 'http://localhost:52000';
const MAX_WAIT_MS = 30_000;
const POLL_INTERVAL_MS = 2_000;

export default async function globalSetup() {
  // 1. Wait for the test endpoint to be available
  console.log('[E2E Setup] Waiting for AgentHub test endpoint...');
  await waitForEndpoint(`${API_URL}/api/test/health`, MAX_WAIT_MS);

  // 2. Trigger a real echo agent conversation
  console.log('[E2E Setup] Triggering echo agent conversation...');
  const response = await fetch(`${API_URL}/api/test/conversations`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      agentName: 'echo-test',
      messages: [
        'What is the architecture of this system?',
        'Can you look up the deployment patterns?',
      ],
    }),
  });

  if (!response.ok) {
    const body = await response.text();
    throw new Error(`[E2E Setup] Echo conversation failed (${response.status}): ${body}`);
  }

  const result = await response.json();
  console.log(
    `[E2E Setup] Conversation seeded: ${result.turnCount} turns, ` +
    `${result.totalToolInvocations} tool invocations, ` +
    `conversationId=${result.conversationId}`
  );

  // 3. Give Prometheus time to scrape the new metrics (default 5s interval + buffer)
  console.log('[E2E Setup] Waiting for metrics scrape interval...');
  await sleep(8_000);

  console.log('[E2E Setup] Ready for tests.');
}

async function waitForEndpoint(url: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    try {
      const res = await fetch(url);
      if (res.ok) return;
    } catch {
      // Server not up yet
    }
    await sleep(POLL_INTERVAL_MS);
  }

  throw new Error(`[E2E Setup] Timed out waiting for ${url} after ${timeoutMs}ms`);
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
