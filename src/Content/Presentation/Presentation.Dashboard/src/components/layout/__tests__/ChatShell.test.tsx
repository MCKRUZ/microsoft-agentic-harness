import { waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import ChatShell from '../ChatShell';
import { renderWithProviders } from '@/test/utils';
import { useAppStore } from '@/stores/appStore';

const mocks = vi.hoisted(() => ({
  agentsData: [] as Array<{ id: string; name: string }>,
}));

// Passthrough provider + a stable connection state so ChatShell renders without
// opening a real SignalR connection.
vi.mock('@/hooks/useAgentHub', () => ({
  useAgentHub: () => ({ connectionState: 'connected' }),
  AgentHubProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/features/agents/useAgentsQuery', () => ({
  useAgentsQuery: () => ({ data: mocks.agentsData }),
}));

describe('ChatShell — default agent selection', () => {
  beforeEach(() => {
    useAppStore.setState({ selectedAgent: null, activeConversationId: null, showSidebar: true });
    mocks.agentsData = [{ id: 'agent-1', name: 'First' }, { id: 'agent-2', name: 'Second' }];
  });

  it('defaults the active agent to the first available one when none is selected', async () => {
    renderWithProviders(<ChatShell />);
    await waitFor(() => expect(useAppStore.getState().selectedAgent).toBe('agent-1'));
  });

  it('does not override an already-selected agent', async () => {
    useAppStore.setState({ selectedAgent: 'agent-2' });
    renderWithProviders(<ChatShell />);
    // Give the effect a chance to (wrongly) fire.
    await new Promise((r) => setTimeout(r, 0));
    expect(useAppStore.getState().selectedAgent).toBe('agent-2');
  });
});
