diff --git a/src/Content/Presentation/Presentation.WebUI/src/app/App.tsx b/src/Content/Presentation/Presentation.WebUI/src/app/App.tsx
index ab505f5..df6b2d4 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/app/App.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/app/App.tsx
@@ -1,9 +1,19 @@
+import { useEffect } from 'react';
+import { useMsal } from '@azure/msal-react';
 import { Providers } from './providers';
 import { AppRouter } from './router';
+import { setMsalInstance } from '@/lib/apiClient';
+
+function MsalInstanceSync() {
+  const { instance } = useMsal();
+  useEffect(() => { setMsalInstance(instance); }, [instance]);
+  return null;
+}
 
 export default function App() {
   return (
     <Providers>
+      <MsalInstanceSync />
       <AppRouter />
     </Providers>
   );
diff --git a/src/Content/Presentation/Presentation.WebUI/src/hooks/__tests__/useAgentHub.test.ts b/src/Content/Presentation/Presentation.WebUI/src/hooks/__tests__/useAgentHub.test.ts
new file mode 100644
index 0000000..55aeef5
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/hooks/__tests__/useAgentHub.test.ts
@@ -0,0 +1,86 @@
+import { renderHook, waitFor } from '@testing-library/react';
+import { vi, describe, it, expect, beforeEach } from 'vitest';
+import { useAgentHub } from '../useAgentHub';
+
+const mocks = vi.hoisted(() => ({
+  connectionStart: vi.fn(),
+  connectionStop: vi.fn(),
+  connectionOn: vi.fn(),
+  connectionOff: vi.fn(),
+  connectionInvoke: vi.fn(),
+  onreconnecting: vi.fn(),
+  onreconnected: vi.fn(),
+  onclose: vi.fn(),
+  buildHubConnection: vi.fn(),
+  acquireTokenSilent: vi.fn(),
+}));
+
+const mockConnection = {
+  start: mocks.connectionStart,
+  stop: mocks.connectionStop,
+  on: mocks.connectionOn,
+  off: mocks.connectionOff,
+  invoke: mocks.connectionInvoke,
+  onreconnecting: mocks.onreconnecting,
+  onreconnected: mocks.onreconnected,
+  onclose: mocks.onclose,
+};
+
+vi.mock('@/lib/signalrClient', () => ({
+  buildHubConnection: mocks.buildHubConnection,
+}));
+
+vi.mock('@azure/msal-react', () => ({
+  useMsal: () => ({
+    instance: { acquireTokenSilent: mocks.acquireTokenSilent },
+    accounts: [{
+      username: 'test@example.com',
+      homeAccountId: '1',
+      environment: '',
+      tenantId: '',
+      localAccountId: '',
+    }],
+  }),
+}));
+
+vi.mock('@/lib/authConfig', () => ({
+  loginRequest: { scopes: ['api://test/access_as_user'] },
+}));
+
+describe('useAgentHub', () => {
+  beforeEach(() => {
+    vi.clearAllMocks();
+    mocks.buildHubConnection.mockReturnValue(mockConnection);
+    mocks.connectionStart.mockResolvedValue(undefined);
+    mocks.connectionStop.mockResolvedValue(undefined);
+    mocks.acquireTokenSilent.mockResolvedValue({ accessToken: 'tok' });
+  });
+
+  it('starts in disconnected state', () => {
+    // connection.start() is pending — state is 'connecting' immediately after mount
+    // which confirms the hook has not yet established a connection
+    mocks.connectionStart.mockReturnValue(new Promise<void>(() => {}));
+
+    const { result } = renderHook(() => useAgentHub());
+
+    expect(result.current.connectionState).not.toBe('connected');
+  });
+
+  it('transitions to connected state after start()', async () => {
+    const { result } = renderHook(() => useAgentHub());
+
+    await waitFor(() => {
+      expect(result.current.connectionState).toBe('connected');
+    });
+  });
+
+  it('cleanup calls connection.stop() on unmount', async () => {
+    const { unmount } = renderHook(() => useAgentHub());
+
+    await waitFor(() => {}); // let effect settle
+
+    unmount();
+
+    expect(mocks.connectionStop).toHaveBeenCalled();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/hooks/__tests__/useAuth.test.ts b/src/Content/Presentation/Presentation.WebUI/src/hooks/__tests__/useAuth.test.ts
new file mode 100644
index 0000000..cc186e2
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/hooks/__tests__/useAuth.test.ts
@@ -0,0 +1,69 @@
+import { renderHook, act } from '@testing-library/react';
+import { vi, describe, it, expect, beforeEach } from 'vitest';
+import { InteractionRequiredAuthError } from '@azure/msal-browser';
+import { useAuth } from '../useAuth';
+
+const mocks = vi.hoisted(() => ({
+  acquireTokenSilent: vi.fn(),
+  acquireTokenPopup: vi.fn(),
+  logoutRedirect: vi.fn(),
+}));
+
+vi.mock('@azure/msal-react', () => ({
+  useMsal: () => ({
+    instance: {
+      acquireTokenSilent: mocks.acquireTokenSilent,
+      acquireTokenPopup: mocks.acquireTokenPopup,
+      logoutRedirect: mocks.logoutRedirect,
+    },
+    accounts: [{
+      username: 'test@example.com',
+      homeAccountId: '1',
+      environment: 'login.microsoftonline.com',
+      tenantId: 'tid',
+      localAccountId: 'lid',
+    }],
+  }),
+}));
+
+vi.mock('@/lib/authConfig', () => ({
+  loginRequest: { scopes: ['api://test-api/access_as_user'] },
+}));
+
+describe('useAuth', () => {
+  beforeEach(() => {
+    vi.clearAllMocks();
+  });
+
+  it('acquireToken returns token from acquireTokenSilent', async () => {
+    mocks.acquireTokenSilent.mockResolvedValue({ accessToken: 'silent-token' });
+
+    const { result } = renderHook(() => useAuth());
+
+    let token!: string;
+    await act(async () => {
+      token = await result.current.acquireToken();
+    });
+
+    expect(token).toBe('silent-token');
+    expect(mocks.acquireTokenSilent).toHaveBeenCalledOnce();
+    expect(mocks.acquireTokenPopup).not.toHaveBeenCalled();
+  });
+
+  it('acquireToken falls back to acquireTokenPopup on InteractionRequiredAuthError', async () => {
+    mocks.acquireTokenSilent.mockRejectedValue(
+      new InteractionRequiredAuthError('interaction_required'),
+    );
+    mocks.acquireTokenPopup.mockResolvedValue({ accessToken: 'popup-token' });
+
+    const { result } = renderHook(() => useAuth());
+
+    let token!: string;
+    await act(async () => {
+      token = await result.current.acquireToken();
+    });
+
+    expect(token).toBe('popup-token');
+    expect(mocks.acquireTokenPopup).toHaveBeenCalledOnce();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts b/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts
index 97697a4..9a84b18 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts
+++ b/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts
@@ -1,19 +1,119 @@
-// Full implementation added in section 09
-
-export interface AgentHubState {
-  connectionState: 'disconnected' | 'connecting' | 'connected';
-  sendMessage: (_conversationId: string, _message: string) => void;
-  startConversation: (_conversationId: string) => void;
-  endConversation: (_conversationId: string) => void;
-  joinGlobalTraces: () => void;
+import { useRef, useState, useEffect } from 'react';
+import { useMsal } from '@azure/msal-react';
+import type { HubConnection } from '@microsoft/signalr';
+import { buildHubConnection } from '@/lib/signalrClient';
+import { loginRequest } from '@/lib/authConfig';
+import { useChatStore, type ChatMessage } from '@/stores/chatStore';
+import { useTelemetryStore } from '@/stores/telemetryStore';
+import type { SpanData } from '@/types/signalr';
+
+export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';
+
+export interface UseAgentHubReturn {
+  connectionState: ConnectionState;
+  sendMessage: (conversationId: string, message: string) => Promise<void>;
+  startConversation: (agentName: string, conversationId: string) => Promise<void>;
+  invokeToolViaAgent: (conversationId: string, toolName: string, args: Record<string, unknown>) => Promise<void>;
+  joinGlobalTraces: () => Promise<void>;
+  leaveGlobalTraces: () => Promise<void>;
 }
 
-export default function useAgentHub(): AgentHubState {
+export function useAgentHub(): UseAgentHubReturn {
+  const { instance, accounts } = useMsal();
+  const [connectionState, setConnectionState] = useState<ConnectionState>('disconnected');
+  const connectionRef = useRef<HubConnection | null>(null);
+  const stoppedRef = useRef(false);
+
+  useEffect(() => {
+    stoppedRef.current = false;
+
+    const account = accounts[0];
+    const getToken = async (): Promise<string> => {
+      if (!account) throw new Error('No account available');
+      const result = await instance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
+      return result.accessToken;
+    };
+
+    const connection = buildHubConnection('/hubs/agent', getToken);
+    connectionRef.current = connection;
+
+    connection.on('TokenReceived', (token: string) => {
+      useChatStore.getState().appendToken(token);
+    });
+
+    connection.on('TurnComplete', (message: ChatMessage) => {
+      useChatStore.getState().finalizeStream(message);
+    });
+
+    connection.on('Error', (message: string) => {
+      useChatStore.getState().setError(message);
+    });
+
+    connection.on('SpanReceived', (span: SpanData) => {
+      useTelemetryStore.getState().addConversationSpan(span.conversationId ?? '', span);
+      useTelemetryStore.getState().addGlobalSpan(span);
+    });
+
+    connection.on('ConversationHistory', (messages: ChatMessage[]) => {
+      useChatStore.getState().setMessages(messages);
+    });
+
+    connection.onreconnecting(() => { setConnectionState('reconnecting'); });
+    connection.onreconnected(() => { setConnectionState('connected'); });
+    connection.onclose(() => { setConnectionState('disconnected'); });
+
+    setConnectionState('connecting');
+    connection.start()
+      .then(() => {
+        if (!stoppedRef.current) setConnectionState('connected');
+      })
+      .catch(() => {
+        if (!stoppedRef.current) setConnectionState('disconnected');
+      });
+
+    return () => {
+      stoppedRef.current = true;
+      connection.off('TokenReceived');
+      connection.off('TurnComplete');
+      connection.off('Error');
+      connection.off('SpanReceived');
+      connection.off('ConversationHistory');
+      void connection.stop();
+      connectionRef.current = null;
+    };
+  // eslint-disable-next-line react-hooks/exhaustive-deps
+  }, []);
+
+  const sendMessage = async (conversationId: string, message: string): Promise<void> => {
+    await connectionRef.current?.invoke('SendMessage', conversationId, message);
+  };
+
+  const startConversation = async (agentName: string, conversationId: string): Promise<void> => {
+    await connectionRef.current?.invoke('StartConversation', agentName, conversationId);
+  };
+
+  const invokeToolViaAgent = async (
+    conversationId: string,
+    toolName: string,
+    args: Record<string, unknown>,
+  ): Promise<void> => {
+    await connectionRef.current?.invoke('InvokeToolViaAgent', conversationId, toolName, args);
+  };
+
+  const joinGlobalTraces = async (): Promise<void> => {
+    await connectionRef.current?.invoke('JoinGlobalTraces');
+  };
+
+  const leaveGlobalTraces = async (): Promise<void> => {
+    await connectionRef.current?.invoke('LeaveGlobalTraces');
+  };
+
   return {
-    connectionState: 'disconnected',
-    sendMessage: () => {},
-    startConversation: () => {},
-    endConversation: () => {},
-    joinGlobalTraces: () => {},
+    connectionState,
+    sendMessage,
+    startConversation,
+    invokeToolViaAgent,
+    joinGlobalTraces,
+    leaveGlobalTraces,
   };
 }
diff --git a/src/Content/Presentation/Presentation.WebUI/src/hooks/useAuth.ts b/src/Content/Presentation/Presentation.WebUI/src/hooks/useAuth.ts
new file mode 100644
index 0000000..0200997
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/hooks/useAuth.ts
@@ -0,0 +1,35 @@
+import { useMsal } from '@azure/msal-react';
+import { InteractionRequiredAuthError, type AccountInfo } from '@azure/msal-browser';
+import { loginRequest } from '@/lib/authConfig';
+
+export interface UseAuthReturn {
+  account: AccountInfo | null;
+  isAuthenticated: boolean;
+  acquireToken: () => Promise<string>;
+  signOut: () => void;
+}
+
+export function useAuth(): UseAuthReturn {
+  const { instance, accounts } = useMsal();
+  const account = accounts[0] ?? null;
+
+  const acquireToken = async (): Promise<string> => {
+    if (!account) throw new Error('No account available');
+    try {
+      const result = await instance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
+      return result.accessToken;
+    } catch (error) {
+      if (error instanceof InteractionRequiredAuthError) {
+        const result = await instance.acquireTokenPopup({ account, scopes: loginRequest.scopes });
+        return result.accessToken;
+      }
+      throw error;
+    }
+  };
+
+  const signOut = (): void => {
+    void instance.logoutRedirect();
+  };
+
+  return { account, isAuthenticated: account !== null, acquireToken, signOut };
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/lib/__tests__/apiClient.test.ts b/src/Content/Presentation/Presentation.WebUI/src/lib/__tests__/apiClient.test.ts
new file mode 100644
index 0000000..736034a
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/lib/__tests__/apiClient.test.ts
@@ -0,0 +1,73 @@
+import axios from 'axios';
+import { vi, describe, it, expect, beforeEach } from 'vitest';
+import type { IPublicClientApplication } from '@azure/msal-browser';
+import { apiClient, setMsalInstance } from '../apiClient';
+
+const mocks = vi.hoisted(() => ({
+  acquireTokenSilent: vi.fn(),
+  loginRedirect: vi.fn(),
+  getAllAccounts: vi.fn(),
+}));
+
+vi.mock('@/lib/authConfig', () => ({
+  loginRequest: { scopes: ['api://test-api/access_as_user'] },
+}));
+
+const mockAccount = {
+  username: 'test@example.com',
+  homeAccountId: '1',
+  environment: 'login.microsoftonline.com',
+  tenantId: 'tid',
+  localAccountId: 'lid',
+};
+
+const mockMsalInstance = {
+  getAllAccounts: mocks.getAllAccounts,
+  acquireTokenSilent: mocks.acquireTokenSilent,
+  loginRedirect: mocks.loginRedirect,
+} as unknown as IPublicClientApplication;
+
+describe('apiClient', () => {
+  beforeEach(() => {
+    vi.clearAllMocks();
+    mocks.getAllAccounts.mockReturnValue([mockAccount]);
+    setMsalInstance(mockMsalInstance);
+  });
+
+  it('attaches Authorization Bearer header to requests', async () => {
+    mocks.acquireTokenSilent.mockResolvedValue({ accessToken: 'test-token' });
+
+    let capturedAuth: string | null = null;
+
+    await apiClient.get('/test', {
+      adapter: async (config) => {
+        capturedAuth = config.headers.get('Authorization') as string | null;
+        return { data: {}, status: 200, statusText: 'OK', headers: {}, config };
+      },
+    });
+
+    expect(capturedAuth).toBe('Bearer test-token');
+  });
+
+  it('redirects to login on 401 response', async () => {
+    mocks.acquireTokenSilent.mockResolvedValue({ accessToken: 'test-token' });
+    mocks.loginRedirect.mockResolvedValue(undefined);
+
+    const error = new axios.AxiosError('Unauthorized', 'ERR_BAD_REQUEST');
+    error.response = {
+      status: 401,
+      data: {},
+      statusText: 'Unauthorized',
+      headers: {},
+      config: { headers: new axios.AxiosHeaders() },
+    };
+
+    await expect(
+      apiClient.get('/test', {
+        adapter: () => Promise.reject(error),
+      }),
+    ).rejects.toThrow();
+
+    expect(mocks.loginRedirect).toHaveBeenCalledOnce();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/lib/__tests__/signalrClient.test.ts b/src/Content/Presentation/Presentation.WebUI/src/lib/__tests__/signalrClient.test.ts
new file mode 100644
index 0000000..9ebdeba
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/lib/__tests__/signalrClient.test.ts
@@ -0,0 +1,43 @@
+import { vi, describe, it, expect } from 'vitest';
+import { buildHubConnection } from '../signalrClient';
+
+const mocks = vi.hoisted(() => {
+  const build = vi.fn(() => ({}));
+  const withUrl = vi.fn();
+  const withAutomaticReconnect = vi.fn();
+  const configureLogging = vi.fn();
+
+  const builder = { withUrl, withAutomaticReconnect, configureLogging, build };
+
+  withUrl.mockReturnValue(builder);
+  withAutomaticReconnect.mockReturnValue(builder);
+  configureLogging.mockReturnValue(builder);
+
+  return { build, withUrl, withAutomaticReconnect, configureLogging };
+});
+
+vi.mock('@microsoft/signalr', () => ({
+  // Must use 'function' (not arrow) so 'new HubConnectionBuilder()' works
+  HubConnectionBuilder: vi.fn(function () {
+    return {
+      withUrl: mocks.withUrl,
+      withAutomaticReconnect: mocks.withAutomaticReconnect,
+      configureLogging: mocks.configureLogging,
+      build: mocks.build,
+    };
+  }),
+  LogLevel: { Warning: 2 },
+}));
+
+describe('buildHubConnection', () => {
+  it('creates connection with accessTokenFactory', () => {
+    const getToken = async () => 'tok';
+
+    buildHubConnection('/hubs/agent', getToken);
+
+    expect(mocks.withUrl).toHaveBeenCalledWith(
+      '/hubs/agent',
+      expect.objectContaining({ accessTokenFactory: getToken }),
+    );
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/lib/apiClient.ts b/src/Content/Presentation/Presentation.WebUI/src/lib/apiClient.ts
index 7abeb0d..cf95a31 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/lib/apiClient.ts
+++ b/src/Content/Presentation/Presentation.WebUI/src/lib/apiClient.ts
@@ -1,8 +1,43 @@
 import axios from 'axios';
+import { InteractionRequiredAuthError, type IPublicClientApplication } from '@azure/msal-browser';
+import { loginRequest } from './authConfig';
 
-// Token interceptor added in section 09
-const apiClient = axios.create({
+let _msalInstance: IPublicClientApplication | null = null;
+
+export function setMsalInstance(instance: IPublicClientApplication): void {
+  _msalInstance = instance;
+}
+
+export const apiClient = axios.create({
   baseURL: import.meta.env['VITE_API_BASE_URL'] as string | undefined,
 });
 
-export default apiClient;
+apiClient.interceptors.request.use(async (config) => {
+  if (!_msalInstance) return config;
+
+  const account = _msalInstance.getAllAccounts()[0];
+  if (!account) return config;
+
+  try {
+    const result = await _msalInstance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
+    config.headers.set('Authorization', `Bearer ${result.accessToken}`);
+  } catch (error) {
+    if (error instanceof InteractionRequiredAuthError) {
+      const result = await _msalInstance.acquireTokenPopup({ account, scopes: loginRequest.scopes });
+      config.headers.set('Authorization', `Bearer ${result.accessToken}`);
+    }
+  }
+
+  return config;
+});
+
+apiClient.interceptors.response.use(
+  (response) => response,
+  async (error: unknown) => {
+    const axiosError = axios.isAxiosError(error) ? error : null;
+    if (axiosError?.response?.status === 401) {
+      await _msalInstance?.loginRedirect(loginRequest);
+    }
+    return Promise.reject(error);
+  },
+);
diff --git a/src/Content/Presentation/Presentation.WebUI/src/lib/authConfig.ts b/src/Content/Presentation/Presentation.WebUI/src/lib/authConfig.ts
index 2105b18..5abce01 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/lib/authConfig.ts
+++ b/src/Content/Presentation/Presentation.WebUI/src/lib/authConfig.ts
@@ -13,7 +13,7 @@ export const msalConfig: Configuration = {
 };
 
 export const loginRequest: PopupRequest = {
-  scopes: [`api://${(import.meta.env['VITE_AZURE_CLIENT_ID'] as string | undefined) ?? ''}/.default`],
+  scopes: [`api://${(import.meta.env['VITE_AZURE_API_CLIENT_ID'] as string | undefined) ?? ''}/access_as_user`],
 };
 
 export const msalInstance = new PublicClientApplication(msalConfig);
diff --git a/src/Content/Presentation/Presentation.WebUI/src/lib/signalrClient.ts b/src/Content/Presentation/Presentation.WebUI/src/lib/signalrClient.ts
index c6cc97c..6cf505e 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/lib/signalrClient.ts
+++ b/src/Content/Presentation/Presentation.WebUI/src/lib/signalrClient.ts
@@ -1,6 +1,12 @@
-import type { HubConnection } from '@microsoft/signalr';
+import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';
 
-// Full implementation added in section 09
-export function buildHubConnection(_url: string): HubConnection {
-  throw new Error('Not implemented — replaced in section 09');
+export function buildHubConnection(
+  path: string,
+  getToken: () => Promise<string>,
+): HubConnection {
+  return new HubConnectionBuilder()
+    .withUrl(path, { accessTokenFactory: getToken })
+    .withAutomaticReconnect([0, 2000, 10000, 30000])
+    .configureLogging(LogLevel.Warning)
+    .build();
 }
diff --git a/src/Content/Presentation/Presentation.WebUI/src/stores/chatStore.ts b/src/Content/Presentation/Presentation.WebUI/src/stores/chatStore.ts
new file mode 100644
index 0000000..9549663
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/stores/chatStore.ts
@@ -0,0 +1,29 @@
+import { create } from 'zustand';
+
+// Full implementation in section 10
+export interface ChatMessage {
+  id: string;
+  role: 'user' | 'assistant' | 'system';
+  content: string;
+  timestamp: string;
+}
+
+interface ChatStore {
+  messages: ChatMessage[];
+  streamingToken: string;
+  error: string | null;
+  appendToken: (token: string) => void;
+  finalizeStream: (message: ChatMessage) => void;
+  setError: (message: string) => void;
+  setMessages: (messages: ChatMessage[]) => void;
+}
+
+export const useChatStore = create<ChatStore>()(() => ({
+  messages: [],
+  streamingToken: '',
+  error: null,
+  appendToken: () => {},
+  finalizeStream: () => {},
+  setError: () => {},
+  setMessages: () => {},
+}));
diff --git a/src/Content/Presentation/Presentation.WebUI/src/stores/telemetryStore.ts b/src/Content/Presentation/Presentation.WebUI/src/stores/telemetryStore.ts
new file mode 100644
index 0000000..dcfc075
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/stores/telemetryStore.ts
@@ -0,0 +1,15 @@
+import { create } from 'zustand';
+import type { SpanData } from '@/types/signalr';
+
+// Full implementation in section 11
+interface TelemetryStore {
+  addConversationSpan: (conversationId: string, span: SpanData) => void;
+  addGlobalSpan: (span: SpanData) => void;
+  clearAll: () => void;
+}
+
+export const useTelemetryStore = create<TelemetryStore>()(() => ({
+  addConversationSpan: () => {},
+  addGlobalSpan: () => {},
+  clearAll: () => {},
+}));
diff --git a/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json b/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json
index 5ac9d4b..66bf259 100644
--- a/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json
+++ b/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json
@@ -28,5 +28,5 @@
     }
   },
   "include": ["src"],
-  "exclude": ["src/test", "src/__tests__"]
+  "exclude": ["src/test", "src/__tests__", "src/**/__tests__"]
 }
diff --git a/src/Content/Presentation/Presentation.WebUI/tsconfig.test.json b/src/Content/Presentation/Presentation.WebUI/tsconfig.test.json
index 4c4cc89..7c09c14 100644
--- a/src/Content/Presentation/Presentation.WebUI/tsconfig.test.json
+++ b/src/Content/Presentation/Presentation.WebUI/tsconfig.test.json
@@ -3,6 +3,6 @@
   "compilerOptions": {
     "types": ["vite/client", "vitest/globals"]
   },
-  "include": ["src/test"],
+  "include": ["src/test", "src/**/__tests__"],
   "exclude": []
 }
