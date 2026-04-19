diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/McpLists.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/McpLists.test.tsx
index 01931c5..9ae59fe 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/McpLists.test.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/McpLists.test.tsx
@@ -1,32 +1,13 @@
-import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
+import { describe, it, expect, vi } from 'vitest';
 
 vi.mock('@/lib/authConfig', () => ({
   loginRequest: { scopes: ['api://test-api/access_as_user'] },
 }));
 import { screen } from '@testing-library/react';
-import { http, HttpResponse } from 'msw';
-import { setupServer } from 'msw/node';
 import { renderWithProviders } from '@/test/utils';
 import { ResourcesList } from '@/features/mcp/ResourcesList';
 import { PromptsList } from '@/features/mcp/PromptsList';
 
-const server = setupServer(
-  http.get('http://localhost/api/mcp/resources', () =>
-    HttpResponse.json([
-      { uri: 'file://docs/readme.md', name: 'Readme', description: 'Project readme' },
-    ]),
-  ),
-  http.get('http://localhost/api/mcp/prompts', () =>
-    HttpResponse.json([
-      { name: 'summarize', description: 'Summarize text', arguments: [{ name: 'text' }] },
-    ]),
-  ),
-);
-
-beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
-afterEach(() => server.resetHandlers());
-afterAll(() => server.close());
-
 describe('ResourcesList', () => {
   it('renders resource URI, name, and description from MSW mock', async () => {
     renderWithProviders(<ResourcesList />);
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/ToolsBrowser.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/ToolsBrowser.test.tsx
index 2113058..bcb0c4f 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/ToolsBrowser.test.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/ToolsBrowser.test.tsx
@@ -1,4 +1,4 @@
-import { describe, it, expect, vi, beforeAll, afterEach, afterAll, beforeEach } from 'vitest';
+import { describe, it, expect, vi, beforeEach } from 'vitest';
 
 vi.mock('@/lib/authConfig', () => ({
   loginRequest: { scopes: ['api://test-api/access_as_user'] },
@@ -6,7 +6,7 @@ vi.mock('@/lib/authConfig', () => ({
 import { screen, waitFor } from '@testing-library/react';
 import userEvent from '@testing-library/user-event';
 import { http, HttpResponse } from 'msw';
-import { setupServer } from 'msw/node';
+import { server } from '@/test/handlers';
 import { renderWithProviders } from '@/test/utils';
 import { ToolsBrowser } from '@/features/mcp/ToolsBrowser';
 import { ToolInvoker } from '@/features/mcp/ToolInvoker';
@@ -25,24 +25,7 @@ vi.mock('@/hooks/useAgentHub', () => ({
   }),
 }));
 
-const sampleTools = [
-  { name: 'get-time', description: 'Gets current time', inputSchema: { type: 'object', properties: {} } },
-  { name: 'calculate', description: 'Performs calculation', inputSchema: { type: 'object', properties: {} } },
-];
-
-const server = setupServer(
-  http.get('http://localhost/api/mcp/tools', () => HttpResponse.json(sampleTools)),
-  http.post('http://localhost/api/mcp/tools/:name/invoke', () =>
-    HttpResponse.json({ result: 'tool executed successfully' }),
-  ),
-);
-
-beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
-afterEach(() => {
-  server.resetHandlers();
-  mockInvokeToolViaAgent.mockClear();
-});
-afterAll(() => server.close());
+const sampleTool = { name: 'get-time', description: 'Gets current time', inputSchema: { type: 'object', properties: {} } };
 
 describe('ToolsBrowser', () => {
   it('renders tool names from MSW mock', async () => {
@@ -61,9 +44,8 @@ describe('ToolsBrowser', () => {
 });
 
 describe('ToolInvoker', () => {
-  const sampleTool = sampleTools[0]!;
-
   beforeEach(() => {
+    vi.clearAllMocks();
     useChatStore.setState({ conversationId: 'test-conv-123' });
   });
 
diff --git a/src/Content/Presentation/Presentation.WebUI/src/test/handlers.ts b/src/Content/Presentation/Presentation.WebUI/src/test/handlers.ts
new file mode 100644
index 0000000..eb4dab7
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/test/handlers.ts
@@ -0,0 +1,33 @@
+import { http, HttpResponse } from 'msw';
+import { setupServer } from 'msw/node';
+
+export const handlers = [
+  http.get('http://localhost/api/agents', () =>
+    HttpResponse.json([{ name: 'research-agent', description: 'A research agent' }]),
+  ),
+
+  http.get('http://localhost/api/mcp/tools', () =>
+    HttpResponse.json([
+      { name: 'get-time', description: 'Gets current time', inputSchema: { type: 'object', properties: {} } },
+      { name: 'calculate', description: 'Performs calculation', inputSchema: { type: 'object', properties: {} } },
+    ]),
+  ),
+
+  http.post('http://localhost/api/mcp/tools/:name/invoke', () =>
+    HttpResponse.json({ result: 'tool executed successfully' }),
+  ),
+
+  http.get('http://localhost/api/mcp/resources', () =>
+    HttpResponse.json([
+      { uri: 'file://docs/readme.md', name: 'Readme', description: 'Project readme' },
+    ]),
+  ),
+
+  http.get('http://localhost/api/mcp/prompts', () =>
+    HttpResponse.json([
+      { name: 'summarize', description: 'Summarize text', arguments: [] },
+    ]),
+  ),
+];
+
+export const server = setupServer(...handlers);
diff --git a/src/Content/Presentation/Presentation.WebUI/src/test/infrastructure.test.ts b/src/Content/Presentation/Presentation.WebUI/src/test/infrastructure.test.ts
new file mode 100644
index 0000000..239ee8f
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/test/infrastructure.test.ts
@@ -0,0 +1,81 @@
+import { describe, it, expect, vi } from 'vitest';
+import { render, screen } from '@testing-library/react';
+import axios from 'axios';
+import { handlers } from './handlers';
+
+vi.mock('@/lib/authConfig', () => ({
+  loginRequest: { scopes: ['api://test-api/access_as_user'] },
+}));
+
+// SignalR mock pattern — class-based so `new HubConnectionBuilder()` works
+const mockOn = vi.fn();
+const mockInvoke = vi.fn().mockResolvedValue(undefined);
+const mockStart = vi.fn().mockResolvedValue(undefined);
+const mockStop = vi.fn().mockResolvedValue(undefined);
+const mockBuild = vi.fn().mockReturnValue({
+  start: mockStart,
+  stop: mockStop,
+  invoke: mockInvoke,
+  on: mockOn,
+  off: vi.fn(),
+  onclose: vi.fn(),
+  state: 'Connected',
+});
+
+vi.mock('@microsoft/signalr', () => {
+  class MockHubConnectionBuilder {
+    withUrl() { return this; }
+    withAutomaticReconnect() { return this; }
+    configureLogging() { return this; }
+    build() { return mockBuild(); }
+  }
+  return {
+    HubConnectionBuilder: MockHubConnectionBuilder,
+    LogLevel: { Information: 1 },
+    HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
+  };
+});
+
+describe('renderWithProviders', () => {
+  it('renders children without crashing', async () => {
+    const { renderWithProviders } = await import('./utils');
+    const { default: React } = await import('react');
+    renderWithProviders(React.createElement('div', null, 'test'));
+    expect(screen.getByText('test')).toBeInTheDocument();
+  });
+});
+
+describe('MSW handlers', () => {
+  it('returns expected fixtures for all /api/* routes', async () => {
+    const routes = [
+      { url: 'http://localhost/api/agents', key: 'name', expected: 'research-agent' },
+      { url: 'http://localhost/api/mcp/tools', key: 'name', expected: 'get-time' },
+      { url: 'http://localhost/api/mcp/resources', key: 'name', expected: 'Readme' },
+      { url: 'http://localhost/api/mcp/prompts', key: 'name', expected: 'summarize' },
+    ];
+
+    for (const { url, key, expected } of routes) {
+      const res = await axios.get<Record<string, string>[]>(url);
+      expect(res.data[0]?.[key]).toBe(expected);
+    }
+  });
+
+  it('exports handlers array with all expected routes', () => {
+    expect(handlers.length).toBeGreaterThanOrEqual(5);
+  });
+});
+
+describe('SignalR mock pattern', () => {
+  it('HubConnectionBuilder mock captures registered event handlers', async () => {
+    const signalr = await import('@microsoft/signalr');
+    const builder = new signalr.HubConnectionBuilder();
+    const conn = builder
+      .withUrl('http://localhost/hubs/agent')
+      .withAutomaticReconnect()
+      .build();
+
+    const handler = vi.fn();
+    conn.on('TestEvent', handler);
+    expect(mockOn).toHaveBeenCalledWith('TestEvent', handler);
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts b/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
index b0ac35c..43787d4 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
+++ b/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
@@ -19,4 +19,8 @@ Object.defineProperty(window, 'matchMedia', {
 // scrollIntoView — not implemented in jsdom
 Element.prototype.scrollIntoView = vi.fn();
 
-// MSW server setup added in section 12
+import { server } from './handlers';
+
+beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
+afterEach(() => server.resetHandlers());
+afterAll(() => server.close());
diff --git a/src/Content/Presentation/Presentation.WebUI/vitest.config.ts b/src/Content/Presentation/Presentation.WebUI/vitest.config.ts
index 9609dd1..c1f7bcc 100644
--- a/src/Content/Presentation/Presentation.WebUI/vitest.config.ts
+++ b/src/Content/Presentation/Presentation.WebUI/vitest.config.ts
@@ -17,6 +17,7 @@ export default defineConfig({
     coverage: {
       provider: 'v8',
       thresholds: { lines: 80, functions: 80, branches: 80, statements: 80 },
+      exclude: ['src/test/**', 'src/components/ui/**', '**/*.d.ts', 'src/types/**'],
     },
   },
   resolve: {
