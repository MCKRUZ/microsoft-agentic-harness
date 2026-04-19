diff --git a/src/Content/Presentation/Presentation.WebUI/src/__tests__/Header.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/__tests__/Header.test.tsx
index 883ba52..2714329 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/__tests__/Header.test.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/__tests__/Header.test.tsx
@@ -3,6 +3,10 @@ import { screen } from '@testing-library/react';
 import { Header } from '@/components/layout/Header';
 import { renderWithProviders } from '@/test/utils';
 
+vi.mock('@/lib/authConfig', () => ({
+  loginRequest: { scopes: ['api://test-api/access_as_user'] },
+}));
+
 vi.mock('@azure/msal-react', () => ({
   useMsal: () => ({
     instance: { logoutRedirect: vi.fn().mockResolvedValue(undefined) },
diff --git a/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx b/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx
index 095db83..e7b7116 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx
@@ -1,13 +1,14 @@
 import { Header } from './Header';
 import { SplitPanel } from './SplitPanel';
 import { ChatPanel } from '@/features/chat/ChatPanel';
+import { RightPanel } from '@/features/telemetry/RightPanel';
 
 export function AppShell() {
   return (
     <div className="flex flex-col h-screen overflow-hidden">
       <Header />
       <div className="flex-1 overflow-hidden min-h-0">
-        <SplitPanel left={<ChatPanel />} right={<div />} />
+        <SplitPanel left={<ChatPanel />} right={<RightPanel />} />
       </div>
     </div>
   );
diff --git a/src/Content/Presentation/Presentation.WebUI/src/components/layout/Header.tsx b/src/Content/Presentation/Presentation.WebUI/src/components/layout/Header.tsx
index 201c43b..5b6c9d1 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/components/layout/Header.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/components/layout/Header.tsx
@@ -1,18 +1,31 @@
 import { Moon, Sun } from 'lucide-react';
 import { useMsal } from '@azure/msal-react';
 import { useTheme } from '@/hooks/useTheme';
+import { useAgentsQuery } from '@/features/agents/useAgentsQuery';
+import { useAppStore } from '@/stores/appStore';
 
 export function Header() {
   const { theme, toggleTheme } = useTheme();
   const { instance, accounts } = useMsal();
   const userName = accounts[0]?.name ?? '';
+  const { data: agents } = useAgentsQuery();
+  const selectedAgent = useAppStore((s) => s.selectedAgent);
+  const setSelectedAgent = useAppStore((s) => s.setSelectedAgent);
 
   return (
     <header className="flex items-center justify-between px-4 h-16 border-b shrink-0">
       <div className="flex items-center gap-4">
         <span className="font-semibold text-lg">AgentHub</span>
-        <select disabled aria-label="Select agent" className="text-sm opacity-50 cursor-not-allowed">
-          <option>No agent selected</option>
+        <select
+          value={selectedAgent ?? ''}
+          onChange={(e) => { if (e.target.value) setSelectedAgent(e.target.value); }}
+          aria-label="Select agent"
+          className="text-sm border rounded px-2 py-1 bg-background"
+        >
+          <option value="" disabled>No agent selected</option>
+          {agents?.map((agent) => (
+            <option key={agent.name} value={agent.name}>{agent.name}</option>
+          ))}
         </select>
       </div>
       <div className="flex items-center gap-2">
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/agents/useAgentsQuery.ts b/src/Content/Presentation/Presentation.WebUI/src/features/agents/useAgentsQuery.ts
new file mode 100644
index 0000000..be161b3
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/agents/useAgentsQuery.ts
@@ -0,0 +1,15 @@
+import { useQuery } from '@tanstack/react-query';
+import { apiClient } from '@/lib/apiClient';
+
+export interface Agent {
+  name: string;
+  description?: string;
+}
+
+export function useAgentsQuery() {
+  return useQuery<Agent[]>({
+    queryKey: ['agents'],
+    queryFn: () => apiClient.get('/api/agents').then((r) => r.data as Agent[]),
+    staleTime: 60_000,
+  });
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatPanel.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatPanel.tsx
index 62b51e2..81cb855 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatPanel.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatPanel.tsx
@@ -1,4 +1,4 @@
-import { useEffect } from 'react';
+import { useEffect, useRef } from 'react';
 import { useChatStore } from './useChatStore';
 import { useAppStore } from '@/stores/appStore';
 import { useAgentHub } from '@/hooks/useAgentHub';
@@ -46,6 +46,7 @@ export function ChatPanel() {
   const selectedAgent = useAppStore((s) => s.selectedAgent);
   const { sendMessage, startConversation } = useAgentHub();
 
+  // Initialize conversation on first mount
   useEffect(() => {
     if (!conversationId) {
       const newId = crypto.randomUUID();
@@ -59,6 +60,21 @@ export function ChatPanel() {
   // eslint-disable-next-line react-hooks/exhaustive-deps
   }, []);
 
+  // Start a new conversation when the selected agent changes
+  const isInitialMount = useRef(true);
+  useEffect(() => {
+    if (isInitialMount.current) {
+      isInitialMount.current = false;
+      return;
+    }
+    if (selectedAgent) {
+      const newId = crypto.randomUUID();
+      setConversationId(newId);
+      void startConversation(selectedAgent, newId).catch(() => {});
+    }
+  // eslint-disable-next-line react-hooks/exhaustive-deps
+  }, [selectedAgent]);
+
   return (
     <div className="flex flex-col h-full">
       <ConversationHeader />
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/mcp/PromptsList.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/mcp/PromptsList.tsx
new file mode 100644
index 0000000..1b51bb2
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/mcp/PromptsList.tsx
@@ -0,0 +1,33 @@
+import { usePromptsQuery } from './useMcpQuery';
+
+export function PromptsList() {
+  const { data: prompts, isLoading, isError } = usePromptsQuery();
+
+  if (isLoading) {
+    return <div className="p-4 text-sm text-muted-foreground">Loading prompts…</div>;
+  }
+
+  if (isError) {
+    return <div className="p-4 text-sm text-destructive">Failed to load prompts.</div>;
+  }
+
+  if (!prompts?.length) {
+    return <div className="p-4 text-sm text-muted-foreground">No prompts available.</div>;
+  }
+
+  return (
+    <ul className="divide-y divide-border">
+      {prompts.map((p) => (
+        <li key={p.name} className="px-3 py-2">
+          <p className="font-semibold text-sm">{p.name}</p>
+          {p.description && <p className="text-xs text-muted-foreground mt-0.5">{p.description}</p>}
+          {p.arguments && p.arguments.length > 0 && (
+            <p className="text-xs text-muted-foreground mt-0.5">
+              Args: {p.arguments.map((a) => a.name).join(', ')}
+            </p>
+          )}
+        </li>
+      ))}
+    </ul>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/mcp/ResourcesList.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/mcp/ResourcesList.tsx
new file mode 100644
index 0000000..4c56c64
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/mcp/ResourcesList.tsx
@@ -0,0 +1,29 @@
+import { useResourcesQuery } from './useMcpQuery';
+
+export function ResourcesList() {
+  const { data: resources, isLoading, isError } = useResourcesQuery();
+
+  if (isLoading) {
+    return <div className="p-4 text-sm text-muted-foreground">Loading resources…</div>;
+  }
+
+  if (isError) {
+    return <div className="p-4 text-sm text-destructive">Failed to load resources.</div>;
+  }
+
+  if (!resources?.length) {
+    return <div className="p-4 text-sm text-muted-foreground">No resources available.</div>;
+  }
+
+  return (
+    <ul className="divide-y divide-border">
+      {resources.map((r) => (
+        <li key={r.uri} className="px-3 py-2">
+          <p className="font-semibold text-sm">{r.name}</p>
+          <p className="font-mono text-xs text-muted-foreground">{r.uri}</p>
+          {r.description && <p className="text-xs text-muted-foreground mt-0.5">{r.description}</p>}
+        </li>
+      ))}
+    </ul>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/mcp/ToolInvoker.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/mcp/ToolInvoker.tsx
new file mode 100644
index 0000000..7a986f6
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/mcp/ToolInvoker.tsx
@@ -0,0 +1,108 @@
+import { useState } from 'react';
+import { useAgentHub } from '@/hooks/useAgentHub';
+import { useChatStore } from '@/stores/chatStore';
+import { useInvokeTool, type McpTool } from './useMcpQuery';
+
+interface ToolInvokerProps {
+  tool: McpTool;
+}
+
+type Mode = 'direct' | 'via-agent';
+
+function formatResult(data: unknown): string {
+  try {
+    return JSON.stringify(data, null, 2);
+  } catch {
+    return String(data);
+  }
+}
+
+export function ToolInvoker({ tool }: ToolInvokerProps) {
+  const [mode, setMode] = useState<Mode>('direct');
+  const [input, setInput] = useState('{}');
+  const [agentResponse, setAgentResponse] = useState<string | null>(null);
+  const [agentError, setAgentError] = useState<string | null>(null);
+
+  const conversationId = useChatStore((s) => s.conversationId);
+  const { invokeToolViaAgent } = useAgentHub();
+  const mutation = useInvokeTool();
+
+  const handleSubmit = async () => {
+    let args: Record<string, unknown>;
+    try {
+      args = JSON.parse(input) as Record<string, unknown>;
+    } catch {
+      setAgentError('Invalid JSON input');
+      return;
+    }
+
+    if (mode === 'direct') {
+      mutation.mutate({ name: tool.name, args });
+    } else {
+      setAgentResponse(null);
+      setAgentError(null);
+      try {
+        await invokeToolViaAgent(conversationId ?? '', tool.name, args);
+        setAgentResponse('Tool invoked via agent. Response will appear in chat.');
+      } catch (err) {
+        setAgentError(err instanceof Error ? err.message : 'Failed to invoke tool via agent');
+      }
+    }
+  };
+
+  const hasError = mode === 'direct' ? mutation.isError : !!agentError;
+  const errorMessage =
+    mode === 'direct'
+      ? (mutation.error?.message ?? 'Request failed')
+      : agentError;
+  const responseData = mode === 'direct' ? mutation.data : agentResponse;
+
+  return (
+    <div className="flex flex-col gap-2 mt-2">
+      <div className="flex gap-1">
+        <button
+          type="button"
+          onClick={() => { setMode('direct'); }}
+          className={`px-3 py-1 text-xs rounded border ${mode === 'direct' ? 'bg-primary text-primary-foreground' : 'hover:bg-accent'}`}
+        >
+          Direct
+        </button>
+        <button
+          type="button"
+          onClick={() => { setMode('via-agent'); }}
+          className={`px-3 py-1 text-xs rounded border ${mode === 'via-agent' ? 'bg-primary text-primary-foreground' : 'hover:bg-accent'}`}
+        >
+          Via Agent
+        </button>
+      </div>
+
+      <textarea
+        value={input}
+        onChange={(e) => { setInput(e.target.value); }}
+        className="font-mono text-xs p-2 border rounded resize-y min-h-[60px] bg-background"
+        spellCheck={false}
+      />
+
+      <button
+        type="button"
+        onClick={() => { void handleSubmit(); }}
+        disabled={mutation.isPending}
+        className="px-3 py-1 text-sm rounded bg-primary text-primary-foreground hover:bg-primary/90 disabled:opacity-50"
+      >
+        {mutation.isPending ? 'Running…' : 'Submit'}
+      </button>
+
+      {hasError && (
+        <div className="text-xs text-destructive p-2 bg-destructive/10 rounded">
+          <span className="font-semibold">Error: </span>{errorMessage}
+        </div>
+      )}
+
+      {responseData != null && !hasError && (
+        <pre className="text-xs p-2 bg-muted rounded overflow-auto max-h-48 break-all whitespace-pre-wrap">
+          {formatResult(responseData)}
+        </pre>
+      )}
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/mcp/ToolsBrowser.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/mcp/ToolsBrowser.tsx
new file mode 100644
index 0000000..66d8b95
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/mcp/ToolsBrowser.tsx
@@ -0,0 +1,50 @@
+import { useState } from 'react';
+import { useToolsQuery, type McpTool } from './useMcpQuery';
+import { ToolInvoker } from './ToolInvoker';
+
+export function ToolsBrowser() {
+  const { data: tools, isLoading, isError } = useToolsQuery();
+  const [selectedTool, setSelectedTool] = useState<McpTool | null>(null);
+
+  if (isLoading) {
+    return <div className="p-4 text-sm text-muted-foreground">Loading tools…</div>;
+  }
+
+  if (isError) {
+    return <div className="p-4 text-sm text-destructive">Failed to load tools.</div>;
+  }
+
+  return (
+    <div className="grid grid-cols-[200px_1fr] h-full overflow-hidden">
+      <div className="border-r overflow-y-auto">
+        {tools?.map((tool) => (
+          <button
+            key={tool.name}
+            type="button"
+            onClick={() => { setSelectedTool(tool); }}
+            className={`w-full text-left px-3 py-2 text-sm border-b hover:bg-accent truncate ${selectedTool?.name === tool.name ? 'bg-accent font-medium' : ''}`}
+          >
+            {tool.name}
+          </button>
+        ))}
+      </div>
+
+      <div className="overflow-y-auto p-3">
+        {selectedTool ? (
+          <>
+            <h3 className="font-semibold text-sm mb-1">{selectedTool.name}</h3>
+            <p className="text-sm text-muted-foreground mb-2">{selectedTool.description}</p>
+            <pre className="text-xs bg-muted p-2 rounded overflow-auto max-h-40 mb-2">
+              {JSON.stringify(selectedTool.inputSchema, null, 2)}
+            </pre>
+            <ToolInvoker tool={selectedTool} />
+          </>
+        ) : (
+          <div className="text-sm text-muted-foreground p-4 text-center">
+            Select a tool to view details and invoke it.
+          </div>
+        )}
+      </div>
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/mcp/useMcpQuery.ts b/src/Content/Presentation/Presentation.WebUI/src/features/mcp/useMcpQuery.ts
new file mode 100644
index 0000000..4c0a21c
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/mcp/useMcpQuery.ts
@@ -0,0 +1,51 @@
+import { useQuery, useMutation } from '@tanstack/react-query';
+import { apiClient } from '@/lib/apiClient';
+
+export interface McpTool {
+  name: string;
+  description: string;
+  inputSchema: Record<string, unknown>;
+}
+
+export interface McpResource {
+  uri: string;
+  name: string;
+  description?: string;
+}
+
+export interface McpPrompt {
+  name: string;
+  description?: string;
+  arguments?: Array<{ name: string; description?: string; required?: boolean }>;
+}
+
+export function useToolsQuery() {
+  return useQuery<McpTool[]>({
+    queryKey: ['mcp', 'tools'],
+    queryFn: () => apiClient.get('/api/mcp/tools').then((r) => r.data as McpTool[]),
+    staleTime: 60_000,
+  });
+}
+
+export function useResourcesQuery() {
+  return useQuery<McpResource[]>({
+    queryKey: ['mcp', 'resources'],
+    queryFn: () => apiClient.get('/api/mcp/resources').then((r) => r.data as McpResource[]),
+    staleTime: 60_000,
+  });
+}
+
+export function usePromptsQuery() {
+  return useQuery<McpPrompt[]>({
+    queryKey: ['mcp', 'prompts'],
+    queryFn: () => apiClient.get('/api/mcp/prompts').then((r) => r.data as McpPrompt[]),
+    staleTime: 60_000,
+  });
+}
+
+export function useInvokeTool() {
+  return useMutation<unknown, Error, { name: string; args: Record<string, unknown> }>({
+    mutationFn: ({ name, args }) =>
+      apiClient.post(`/api/mcp/tools/${name}/invoke`, { args }).then((r) => r.data),
+  });
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/RightPanel.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/RightPanel.tsx
new file mode 100644
index 0000000..3818971
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/RightPanel.tsx
@@ -0,0 +1,65 @@
+import { useState } from 'react';
+import { useTelemetryStore } from '@/stores/telemetryStore';
+import { useAppStore } from '@/stores/appStore';
+import { TracesPanel } from './TracesPanel';
+import { ToolsBrowser } from '@/features/mcp/ToolsBrowser';
+import { ResourcesList } from '@/features/mcp/ResourcesList';
+import { PromptsList } from '@/features/mcp/PromptsList';
+
+const TABS = [
+  { value: 'my-traces', label: 'My Traces' },
+  { value: 'all-traces', label: 'All Traces' },
+  { value: 'tools', label: 'Tools' },
+  { value: 'resources', label: 'Resources' },
+  { value: 'prompts', label: 'Prompts' },
+] as const;
+
+type TabValue = (typeof TABS)[number]['value'];
+
+export function RightPanel() {
+  const [activeTab, setActiveTab] = useState<TabValue>('my-traces');
+
+  const activeConversationId = useAppStore((s) => s.selectedAgent);
+  const conversationSpans = useTelemetryStore((s) => s.conversationSpans);
+  const globalSpans = useTelemetryStore((s) => s.globalSpans);
+  const clearConversation = useTelemetryStore((s) => s.clearConversation);
+  const clearAll = useTelemetryStore((s) => s.clearAll);
+
+  const mySpans = (activeConversationId ? (conversationSpans[activeConversationId] ?? []) : []);
+
+  return (
+    <div className="flex flex-col h-full">
+      <div className="sticky top-0 z-10 flex border-b shrink-0 bg-background overflow-x-auto">
+        {TABS.map((tab) => (
+          <button
+            key={tab.value}
+            type="button"
+            onClick={() => { setActiveTab(tab.value); }}
+            className={`px-3 py-2 text-sm whitespace-nowrap border-b-2 -mb-px transition-colors ${
+              activeTab === tab.value
+                ? 'border-primary text-foreground font-medium'
+                : 'border-transparent text-muted-foreground hover:text-foreground'
+            }`}
+          >
+            {tab.label}
+          </button>
+        ))}
+      </div>
+
+      <div className="flex-1 overflow-y-auto min-h-0">
+        {activeTab === 'my-traces' && (
+          <TracesPanel
+            spans={mySpans}
+            onClear={activeConversationId ? () => { clearConversation(activeConversationId); } : undefined}
+          />
+        )}
+        {activeTab === 'all-traces' && (
+          <TracesPanel spans={globalSpans} onClear={clearAll} />
+        )}
+        {activeTab === 'tools' && <ToolsBrowser />}
+        {activeTab === 'resources' && <ResourcesList />}
+        {activeTab === 'prompts' && <PromptsList />}
+      </div>
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/SpanDetail.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/SpanDetail.tsx
new file mode 100644
index 0000000..1efc8e6
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/SpanDetail.tsx
@@ -0,0 +1,33 @@
+import type { SpanTreeNode } from './types';
+
+interface SpanDetailProps {
+  span: SpanTreeNode;
+}
+
+export function SpanDetail({ span }: SpanDetailProps) {
+  const entries = Object.entries(span.tags);
+
+  return (
+    <div className="px-2 py-1 text-xs bg-muted/40 rounded mb-1">
+      {span.statusDescription && (
+        <p className="text-muted-foreground mb-1 break-all">{span.statusDescription}</p>
+      )}
+      {entries.length > 0 ? (
+        <table className="w-full border-collapse">
+          <tbody>
+            {entries.map(([key, value]) => (
+              <tr key={key} className="border-b border-border/30 last:border-0">
+                <td className="py-0.5 pr-3 font-mono text-muted-foreground whitespace-nowrap align-top">
+                  {key}
+                </td>
+                <td className="py-0.5 font-mono break-all">{value}</td>
+              </tr>
+            ))}
+          </tbody>
+        </table>
+      ) : (
+        <span className="text-muted-foreground">No tags</span>
+      )}
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/SpanNode.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/SpanNode.tsx
new file mode 100644
index 0000000..bd7160d
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/SpanNode.tsx
@@ -0,0 +1,52 @@
+import { useState } from 'react';
+import type { SpanTreeNode } from './types';
+import { SpanDetail } from './SpanDetail';
+
+const STATUS_DOT: Record<string, string> = {
+  ok: 'bg-green-500',
+  error: 'bg-red-500',
+  unset: 'bg-gray-400',
+};
+
+interface SpanNodeProps {
+  node: SpanTreeNode;
+  rootDurationMs: number;
+  depth?: number;
+}
+
+export function SpanNode({ node, rootDurationMs, depth = 0 }: SpanNodeProps) {
+  const [expanded, setExpanded] = useState(false);
+  const dotClass = STATUS_DOT[node.status] ?? 'bg-gray-400';
+  const barWidth = rootDurationMs > 0 ? Math.min(100, (node.durationMs / rootDurationMs) * 100) : 0;
+
+  return (
+    <div style={{ marginLeft: `${depth * 16}px` }}>
+      <button
+        type="button"
+        onClick={() => { setExpanded((v) => !v); }}
+        className="flex items-center gap-2 w-full text-left px-1 py-0.5 rounded hover:bg-accent text-xs"
+      >
+        <span className={`shrink-0 w-2 h-2 rounded-full ${dotClass}`} />
+        <div className="relative flex-1 min-w-0">
+          <div
+            className={`absolute inset-y-0 left-0 rounded opacity-20 ${dotClass}`}
+            style={{ width: `${barWidth}%` }}
+          />
+          <span className="relative truncate">{node.name}</span>
+        </div>
+        <span className="shrink-0 text-muted-foreground">({node.durationMs}ms)</span>
+      </button>
+
+      {expanded && <SpanDetail span={node} />}
+
+      {node.children.map((child) => (
+        <SpanNode
+          key={child.spanId}
+          node={child}
+          rootDurationMs={rootDurationMs}
+          depth={(depth ?? 0) + 1}
+        />
+      ))}
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/SpanTree.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/SpanTree.tsx
new file mode 100644
index 0000000..8cb3b1b
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/SpanTree.tsx
@@ -0,0 +1,14 @@
+import type { SpanTreeNode } from './types';
+import { SpanNode } from './SpanNode';
+
+interface SpanTreeProps {
+  node: SpanTreeNode;
+}
+
+export function SpanTree({ node }: SpanTreeProps) {
+  return (
+    <div data-testid="span-tree" className="border-b border-border/30 py-1 last:border-0">
+      <SpanNode node={node} rootDurationMs={node.durationMs} depth={0} />
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/TracesPanel.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/TracesPanel.tsx
new file mode 100644
index 0000000..380684a
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/TracesPanel.tsx
@@ -0,0 +1,47 @@
+import { useMemo } from 'react';
+import type { SpanData } from '@/types/signalr';
+import { buildSpanTree } from './buildSpanTree';
+import { SpanTree } from './SpanTree';
+
+interface TracesPanelProps {
+  spans: SpanData[];
+  onClear?: () => void;
+}
+
+export function TracesPanel({ spans, onClear }: TracesPanelProps) {
+  const roots = useMemo(() => buildSpanTree(spans), [spans]);
+
+  if (roots.length === 0) {
+    return (
+      <div className="flex flex-col h-full">
+        {onClear && (
+          <div className="flex justify-end px-2 py-1 border-b shrink-0">
+            <button type="button" onClick={onClear} className="text-xs text-muted-foreground hover:text-foreground">
+              Clear
+            </button>
+          </div>
+        )}
+        <div className="flex-1 flex items-center justify-center text-sm text-muted-foreground p-4 text-center">
+          No traces yet. Run an agent turn to see spans here.
+        </div>
+      </div>
+    );
+  }
+
+  return (
+    <div className="flex flex-col h-full">
+      {onClear && (
+        <div className="flex justify-end px-2 py-1 border-b shrink-0">
+          <button type="button" onClick={onClear} className="text-xs text-muted-foreground hover:text-foreground">
+            Clear
+          </button>
+        </div>
+      )}
+      <div className="flex-1 overflow-y-auto px-1">
+        {roots.map((root) => (
+          <SpanTree key={root.spanId} node={root} />
+        ))}
+      </div>
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/McpLists.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/McpLists.test.tsx
new file mode 100644
index 0000000..01931c5
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/McpLists.test.tsx
@@ -0,0 +1,45 @@
+import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';
+
+vi.mock('@/lib/authConfig', () => ({
+  loginRequest: { scopes: ['api://test-api/access_as_user'] },
+}));
+import { screen } from '@testing-library/react';
+import { http, HttpResponse } from 'msw';
+import { setupServer } from 'msw/node';
+import { renderWithProviders } from '@/test/utils';
+import { ResourcesList } from '@/features/mcp/ResourcesList';
+import { PromptsList } from '@/features/mcp/PromptsList';
+
+const server = setupServer(
+  http.get('http://localhost/api/mcp/resources', () =>
+    HttpResponse.json([
+      { uri: 'file://docs/readme.md', name: 'Readme', description: 'Project readme' },
+    ]),
+  ),
+  http.get('http://localhost/api/mcp/prompts', () =>
+    HttpResponse.json([
+      { name: 'summarize', description: 'Summarize text', arguments: [{ name: 'text' }] },
+    ]),
+  ),
+);
+
+beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
+afterEach(() => server.resetHandlers());
+afterAll(() => server.close());
+
+describe('ResourcesList', () => {
+  it('renders resource URI, name, and description from MSW mock', async () => {
+    renderWithProviders(<ResourcesList />);
+    await screen.findByText('Readme');
+    expect(screen.getByText('file://docs/readme.md')).toBeInTheDocument();
+    expect(screen.getByText('Project readme')).toBeInTheDocument();
+  });
+});
+
+describe('PromptsList', () => {
+  it('renders prompt name and description from MSW mock', async () => {
+    renderWithProviders(<PromptsList />);
+    await screen.findByText('summarize');
+    expect(screen.getByText('Summarize text')).toBeInTheDocument();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/SpanNode.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/SpanNode.test.tsx
new file mode 100644
index 0000000..bc24762
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/SpanNode.test.tsx
@@ -0,0 +1,49 @@
+import { describe, it, expect } from 'vitest';
+import { screen } from '@testing-library/react';
+import userEvent from '@testing-library/user-event';
+import { renderWithProviders } from '@/test/utils';
+import { SpanNode } from '../SpanNode';
+import type { SpanTreeNode } from '../types';
+
+function makeNode(status: 'ok' | 'error' | 'unset', tags: Record<string, string> = {}): SpanTreeNode {
+  return {
+    name: 'test-span',
+    traceId: 'trace-1',
+    spanId: 'span-1',
+    parentSpanId: null,
+    conversationId: null,
+    startTime: '2024-01-01T00:00:00.000Z',
+    durationMs: 100,
+    status,
+    kind: 'internal',
+    sourceName: 'test',
+    tags,
+    children: [],
+  };
+}
+
+describe('SpanNode', () => {
+  it('renders green status indicator for ok status', () => {
+    renderWithProviders(<SpanNode node={makeNode('ok')} rootDurationMs={100} depth={0} />);
+    expect(document.querySelector('.bg-green-500')).toBeTruthy();
+  });
+
+  it('renders red status indicator for error status', () => {
+    renderWithProviders(<SpanNode node={makeNode('error')} rootDurationMs={100} depth={0} />);
+    expect(document.querySelector('.bg-red-500')).toBeTruthy();
+  });
+
+  it('renders grey status indicator for unset status', () => {
+    renderWithProviders(<SpanNode node={makeNode('unset')} rootDurationMs={100} depth={0} />);
+    expect(document.querySelector('.bg-gray-400')).toBeTruthy();
+  });
+
+  it('Clicking SpanNode expands SpanDetail showing tags as key-value pairs', async () => {
+    const user = userEvent.setup();
+    const node = makeNode('ok', { 'http.method': 'GET', 'http.url': '/api/test' });
+    renderWithProviders(<SpanNode node={node} rootDurationMs={100} depth={0} />);
+    await user.click(screen.getByRole('button'));
+    expect(screen.getByText('http.method')).toBeInTheDocument();
+    expect(screen.getByText('GET')).toBeInTheDocument();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/ToolsBrowser.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/ToolsBrowser.test.tsx
new file mode 100644
index 0000000..2113058
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/ToolsBrowser.test.tsx
@@ -0,0 +1,111 @@
+import { describe, it, expect, vi, beforeAll, afterEach, afterAll, beforeEach } from 'vitest';
+
+vi.mock('@/lib/authConfig', () => ({
+  loginRequest: { scopes: ['api://test-api/access_as_user'] },
+}));
+import { screen, waitFor } from '@testing-library/react';
+import userEvent from '@testing-library/user-event';
+import { http, HttpResponse } from 'msw';
+import { setupServer } from 'msw/node';
+import { renderWithProviders } from '@/test/utils';
+import { ToolsBrowser } from '@/features/mcp/ToolsBrowser';
+import { ToolInvoker } from '@/features/mcp/ToolInvoker';
+import { useChatStore } from '@/stores/chatStore';
+
+const mockInvokeToolViaAgent = vi.fn().mockResolvedValue(undefined);
+
+vi.mock('@/hooks/useAgentHub', () => ({
+  useAgentHub: () => ({
+    connectionState: 'connected' as const,
+    sendMessage: vi.fn(),
+    startConversation: vi.fn(),
+    invokeToolViaAgent: mockInvokeToolViaAgent,
+    joinGlobalTraces: vi.fn(),
+    leaveGlobalTraces: vi.fn(),
+  }),
+}));
+
+const sampleTools = [
+  { name: 'get-time', description: 'Gets current time', inputSchema: { type: 'object', properties: {} } },
+  { name: 'calculate', description: 'Performs calculation', inputSchema: { type: 'object', properties: {} } },
+];
+
+const server = setupServer(
+  http.get('http://localhost/api/mcp/tools', () => HttpResponse.json(sampleTools)),
+  http.post('http://localhost/api/mcp/tools/:name/invoke', () =>
+    HttpResponse.json({ result: 'tool executed successfully' }),
+  ),
+);
+
+beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
+afterEach(() => {
+  server.resetHandlers();
+  mockInvokeToolViaAgent.mockClear();
+});
+afterAll(() => server.close());
+
+describe('ToolsBrowser', () => {
+  it('renders tool names from MSW mock', async () => {
+    renderWithProviders(<ToolsBrowser />);
+    await screen.findByText('get-time');
+    expect(screen.getByText('calculate')).toBeInTheDocument();
+  });
+
+  it('Clicking a tool shows its description and schema', async () => {
+    const user = userEvent.setup();
+    renderWithProviders(<ToolsBrowser />);
+    await screen.findByText('get-time');
+    await user.click(screen.getByText('get-time'));
+    expect(screen.getByText('Gets current time')).toBeInTheDocument();
+  });
+});
+
+describe('ToolInvoker', () => {
+  const sampleTool = sampleTools[0]!;
+
+  beforeEach(() => {
+    useChatStore.setState({ conversationId: 'test-conv-123' });
+  });
+
+  it('Direct mode submit calls useInvokeTool mutation', async () => {
+    const user = userEvent.setup();
+    renderWithProviders(<ToolInvoker tool={sampleTool} />);
+    await user.click(screen.getByRole('button', { name: /submit/i }));
+    await waitFor(() => {
+      expect(screen.getByText(/tool executed successfully/i)).toBeInTheDocument();
+    });
+  });
+
+  it('Via Agent mode submit calls invokeToolViaAgent on the hub', async () => {
+    const user = userEvent.setup();
+    renderWithProviders(<ToolInvoker tool={sampleTool} />);
+    await user.click(screen.getByRole('button', { name: /via agent/i }));
+    await user.click(screen.getByRole('button', { name: /submit/i }));
+    await waitFor(() => {
+      expect(mockInvokeToolViaAgent).toHaveBeenCalledWith('test-conv-123', 'get-time', {});
+    });
+  });
+
+  it('shows response after successful invocation', async () => {
+    const user = userEvent.setup();
+    renderWithProviders(<ToolInvoker tool={sampleTool} />);
+    await user.click(screen.getByRole('button', { name: /submit/i }));
+    await waitFor(() => {
+      expect(screen.getByText(/tool executed successfully/i)).toBeInTheDocument();
+    });
+  });
+
+  it('shows error message after failed invocation', async () => {
+    server.use(
+      http.post('http://localhost/api/mcp/tools/:name/invoke', () =>
+        HttpResponse.json({ error: 'Tool not found' }, { status: 404 }),
+      ),
+    );
+    const user = userEvent.setup();
+    renderWithProviders(<ToolInvoker tool={sampleTool} />);
+    await user.click(screen.getByRole('button', { name: /submit/i }));
+    await waitFor(() => {
+      expect(screen.getByText(/error/i)).toBeInTheDocument();
+    });
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/TracesPanel.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/TracesPanel.test.tsx
new file mode 100644
index 0000000..edf4ee6
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/TracesPanel.test.tsx
@@ -0,0 +1,38 @@
+import { describe, it, expect } from 'vitest';
+import { screen } from '@testing-library/react';
+import { renderWithProviders } from '@/test/utils';
+import { TracesPanel } from '../TracesPanel';
+import type { SpanData } from '@/types/signalr';
+
+function makeSpan(spanId: string, parentSpanId: string | null, traceId = 'trace-1'): SpanData {
+  return {
+    name: `span-${spanId}`,
+    traceId,
+    spanId,
+    parentSpanId,
+    conversationId: null,
+    startTime: '2024-01-01T00:00:00.000Z',
+    durationMs: 100,
+    status: 'ok',
+    kind: 'internal',
+    sourceName: 'test',
+    tags: {},
+  };
+}
+
+describe('TracesPanel', () => {
+  it('with empty spans array renders empty state placeholder text', () => {
+    renderWithProviders(<TracesPanel spans={[]} />);
+    expect(screen.getByText(/No traces yet/i)).toBeInTheDocument();
+  });
+
+  it('renders correct number of root SpanTree components for disjoint traces', () => {
+    const spans = [
+      makeSpan('root1', null, 'trace-1'),
+      makeSpan('root2', null, 'trace-2'),
+      makeSpan('root3', null, 'trace-3'),
+    ];
+    renderWithProviders(<TracesPanel spans={spans} />);
+    expect(document.querySelectorAll('[data-testid="span-tree"]')).toHaveLength(3);
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/buildSpanTree.test.ts b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/buildSpanTree.test.ts
new file mode 100644
index 0000000..bb6b54a
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/buildSpanTree.test.ts
@@ -0,0 +1,62 @@
+import { describe, it, expect } from 'vitest';
+import { buildSpanTree } from '../buildSpanTree';
+import type { SpanData } from '@/types/signalr';
+
+function makeSpan(spanId: string, parentSpanId: string | null, traceId = 'trace-1'): SpanData {
+  return {
+    name: `span-${spanId}`,
+    traceId,
+    spanId,
+    parentSpanId,
+    conversationId: null,
+    startTime: '2024-01-01T00:00:00.000Z',
+    durationMs: 100,
+    status: 'ok',
+    kind: 'internal',
+    sourceName: 'test',
+    tags: {},
+  };
+}
+
+describe('buildSpanTree', () => {
+  it('returns empty array for empty input', () => {
+    expect(buildSpanTree([])).toEqual([]);
+  });
+
+  it('nests child spans under their parent by parentSpanId', () => {
+    const spans = [makeSpan('root', null), makeSpan('child', 'root')];
+    const result = buildSpanTree(spans);
+    expect(result).toHaveLength(1);
+    expect(result[0].spanId).toBe('root');
+    expect(result[0].children).toHaveLength(1);
+    expect(result[0].children[0].spanId).toBe('child');
+  });
+
+  it('handles root spans with null parentSpanId', () => {
+    const spans = [makeSpan('root', null)];
+    const result = buildSpanTree(spans);
+    expect(result).toHaveLength(1);
+    expect(result[0].parentSpanId).toBeNull();
+    expect(result[0].children).toEqual([]);
+  });
+
+  it('handles multiple disjoint trace trees', () => {
+    const spans = [
+      makeSpan('root1', null, 'trace-1'),
+      makeSpan('root2', null, 'trace-2'),
+      makeSpan('child1', 'root1', 'trace-1'),
+    ];
+    const result = buildSpanTree(spans);
+    expect(result).toHaveLength(2);
+    const root1 = result.find((r) => r.spanId === 'root1');
+    expect(root1?.children).toHaveLength(1);
+  });
+
+  it('result is stable for same input', () => {
+    const spans = [makeSpan('root', null), makeSpan('child', 'root')];
+    const result1 = buildSpanTree(spans);
+    const result2 = buildSpanTree(spans);
+    expect(result1[0].spanId).toBe(result2[0].spanId);
+    expect(result1[0].children[0].spanId).toBe(result2[0].children[0].spanId);
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/useTelemetryStore.test.ts b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/useTelemetryStore.test.ts
new file mode 100644
index 0000000..f0a5a1b
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/__tests__/useTelemetryStore.test.ts
@@ -0,0 +1,46 @@
+import { describe, it, expect, beforeEach } from 'vitest';
+import { useTelemetryStore } from '@/stores/telemetryStore';
+import type { SpanData } from '@/types/signalr';
+
+function makeSpan(spanId: string): SpanData {
+  return {
+    name: `span-${spanId}`,
+    traceId: 'trace-1',
+    spanId,
+    parentSpanId: null,
+    conversationId: null,
+    startTime: '2024-01-01T00:00:00.000Z',
+    durationMs: 10,
+    status: 'ok',
+    kind: 'internal',
+    sourceName: 'test',
+    tags: {},
+  };
+}
+
+describe('useTelemetryStore', () => {
+  beforeEach(() => {
+    useTelemetryStore.setState({ conversationSpans: {}, globalSpans: [] });
+  });
+
+  it('addGlobalSpan caps at MAX_GLOBAL_SPANS (500), dropping oldest entries', () => {
+    const { addGlobalSpan } = useTelemetryStore.getState();
+    for (let i = 0; i < 501; i++) {
+      addGlobalSpan(makeSpan(`span-${i}`));
+    }
+    const { globalSpans } = useTelemetryStore.getState();
+    expect(globalSpans).toHaveLength(500);
+    expect(globalSpans[0].spanId).toBe('span-1');
+    expect(globalSpans[499].spanId).toBe('span-500');
+  });
+
+  it('clearAll resets both conversationSpans and globalSpans to empty', () => {
+    const { addConversationSpan, addGlobalSpan, clearAll } = useTelemetryStore.getState();
+    addConversationSpan('conv1', makeSpan('s1'));
+    addGlobalSpan(makeSpan('s2'));
+    clearAll();
+    const state = useTelemetryStore.getState();
+    expect(state.conversationSpans).toEqual({});
+    expect(state.globalSpans).toEqual([]);
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/buildSpanTree.ts b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/buildSpanTree.ts
new file mode 100644
index 0000000..ae82c04
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/buildSpanTree.ts
@@ -0,0 +1,22 @@
+import type { SpanData } from '@/types/signalr';
+import type { SpanTreeNode } from './types';
+
+export function buildSpanTree(spans: SpanData[]): SpanTreeNode[] {
+  const nodeMap = new Map<string, SpanTreeNode>();
+
+  for (const span of spans) {
+    nodeMap.set(span.spanId, { ...span, children: [] });
+  }
+
+  const roots: SpanTreeNode[] = [];
+
+  for (const node of nodeMap.values()) {
+    if (node.parentSpanId !== null && nodeMap.has(node.parentSpanId)) {
+      nodeMap.get(node.parentSpanId)!.children.push(node);
+    } else {
+      roots.push(node);
+    }
+  }
+
+  return roots;
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/types.ts b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/types.ts
new file mode 100644
index 0000000..6867dd7
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/telemetry/types.ts
@@ -0,0 +1,7 @@
+import type { SpanData } from '@/types/signalr';
+
+export type { SpanData };
+
+export interface SpanTreeNode extends SpanData {
+  children: SpanTreeNode[];
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/stores/telemetryStore.ts b/src/Content/Presentation/Presentation.WebUI/src/stores/telemetryStore.ts
index dcfc075..8e8d3ba 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/stores/telemetryStore.ts
+++ b/src/Content/Presentation/Presentation.WebUI/src/stores/telemetryStore.ts
@@ -1,15 +1,43 @@
 import { create } from 'zustand';
 import type { SpanData } from '@/types/signalr';
 
-// Full implementation in section 11
-interface TelemetryStore {
+const MAX_GLOBAL_SPANS = 500;
+
+interface TelemetryState {
+  conversationSpans: Record<string, SpanData[]>;
+  globalSpans: SpanData[];
   addConversationSpan: (conversationId: string, span: SpanData) => void;
   addGlobalSpan: (span: SpanData) => void;
+  clearConversation: (conversationId: string) => void;
   clearAll: () => void;
 }
 
-export const useTelemetryStore = create<TelemetryStore>()(() => ({
-  addConversationSpan: () => {},
-  addGlobalSpan: () => {},
-  clearAll: () => {},
+export const useTelemetryStore = create<TelemetryState>()((set) => ({
+  conversationSpans: {},
+  globalSpans: [],
+
+  addConversationSpan: (conversationId, span) =>
+    set((state) => ({
+      conversationSpans: {
+        ...state.conversationSpans,
+        [conversationId]: [...(state.conversationSpans[conversationId] ?? []), span],
+      },
+    })),
+
+  addGlobalSpan: (span) =>
+    set((state) => {
+      const updated = [...state.globalSpans, span];
+      return {
+        globalSpans: updated.length > MAX_GLOBAL_SPANS ? updated.slice(-MAX_GLOBAL_SPANS) : updated,
+      };
+    }),
+
+  clearConversation: (conversationId) =>
+    set((state) => ({
+      conversationSpans: Object.fromEntries(
+        Object.entries(state.conversationSpans).filter(([k]) => k !== conversationId),
+      ),
+    })),
+
+  clearAll: () => set({ conversationSpans: {}, globalSpans: [] }),
 }));
diff --git a/src/Content/Presentation/Presentation.WebUI/vitest.config.ts b/src/Content/Presentation/Presentation.WebUI/vitest.config.ts
index bea09db..9609dd1 100644
--- a/src/Content/Presentation/Presentation.WebUI/vitest.config.ts
+++ b/src/Content/Presentation/Presentation.WebUI/vitest.config.ts
@@ -8,6 +8,9 @@ export default defineConfig({
     environment: 'jsdom',
     globals: true,
     setupFiles: ['./src/test/setup.ts'],
+    environmentOptions: {
+      jsdom: { url: 'http://localhost' },
+    },
     typecheck: {
       tsconfig: './tsconfig.test.json',
     },
