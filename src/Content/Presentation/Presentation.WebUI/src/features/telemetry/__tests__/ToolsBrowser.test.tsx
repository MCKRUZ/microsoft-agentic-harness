import { describe, it, expect, vi, beforeAll, afterEach, afterAll, beforeEach } from 'vitest';

vi.mock('@/lib/authConfig', () => ({
  loginRequest: { scopes: ['api://test-api/access_as_user'] },
}));
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { renderWithProviders } from '@/test/utils';
import { ToolsBrowser } from '@/features/mcp/ToolsBrowser';
import { ToolInvoker } from '@/features/mcp/ToolInvoker';
import { useChatStore } from '@/stores/chatStore';

const mockInvokeToolViaAgent = vi.fn().mockResolvedValue(undefined);

vi.mock('@/hooks/useAgentHub', () => ({
  useAgentHub: () => ({
    connectionState: 'connected' as const,
    sendMessage: vi.fn(),
    startConversation: vi.fn(),
    invokeToolViaAgent: mockInvokeToolViaAgent,
    joinGlobalTraces: vi.fn(),
    leaveGlobalTraces: vi.fn(),
  }),
}));

const sampleTools = [
  { name: 'get-time', description: 'Gets current time', inputSchema: { type: 'object', properties: {} } },
  { name: 'calculate', description: 'Performs calculation', inputSchema: { type: 'object', properties: {} } },
];

const server = setupServer(
  http.get('http://localhost/api/mcp/tools', () => HttpResponse.json(sampleTools)),
  http.post('http://localhost/api/mcp/tools/:name/invoke', () =>
    HttpResponse.json({ result: 'tool executed successfully' }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
afterEach(() => {
  server.resetHandlers();
  mockInvokeToolViaAgent.mockClear();
});
afterAll(() => server.close());

describe('ToolsBrowser', () => {
  it('renders tool names from MSW mock', async () => {
    renderWithProviders(<ToolsBrowser />);
    await screen.findByText('get-time');
    expect(screen.getByText('calculate')).toBeInTheDocument();
  });

  it('Clicking a tool shows its description and schema', async () => {
    const user = userEvent.setup();
    renderWithProviders(<ToolsBrowser />);
    await screen.findByText('get-time');
    await user.click(screen.getByText('get-time'));
    expect(screen.getByText('Gets current time')).toBeInTheDocument();
  });
});

describe('ToolInvoker', () => {
  const sampleTool = sampleTools[0]!;

  beforeEach(() => {
    useChatStore.setState({ conversationId: 'test-conv-123' });
  });

  it('Direct mode submit calls useInvokeTool mutation', async () => {
    const user = userEvent.setup();
    renderWithProviders(<ToolInvoker tool={sampleTool} />);
    await user.click(screen.getByRole('button', { name: /submit/i }));
    await waitFor(() => {
      expect(screen.getByText(/tool executed successfully/i)).toBeInTheDocument();
    });
  });

  it('Via Agent mode submit calls invokeToolViaAgent on the hub', async () => {
    const user = userEvent.setup();
    renderWithProviders(<ToolInvoker tool={sampleTool} />);
    await user.click(screen.getByRole('button', { name: /via agent/i }));
    await user.click(screen.getByRole('button', { name: /submit/i }));
    await waitFor(() => {
      expect(mockInvokeToolViaAgent).toHaveBeenCalledWith('test-conv-123', 'get-time', {});
    });
  });

  it('shows response after successful invocation', async () => {
    const user = userEvent.setup();
    renderWithProviders(<ToolInvoker tool={sampleTool} />);
    await user.click(screen.getByRole('button', { name: /submit/i }));
    await waitFor(() => {
      expect(screen.getByText(/tool executed successfully/i)).toBeInTheDocument();
    });
  });

  it('shows error message after failed invocation', async () => {
    server.use(
      http.post('http://localhost/api/mcp/tools/:name/invoke', () =>
        HttpResponse.json({ error: 'Tool not found' }, { status: 404 }),
      ),
    );
    const user = userEvent.setup();
    renderWithProviders(<ToolInvoker tool={sampleTool} />);
    await user.click(screen.getByRole('button', { name: /submit/i }));
    await waitFor(() => {
      expect(screen.getByText(/error/i)).toBeInTheDocument();
    });
  });
});
