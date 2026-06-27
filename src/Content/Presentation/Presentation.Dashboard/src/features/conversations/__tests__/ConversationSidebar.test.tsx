import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { ConversationSidebar } from '../ConversationSidebar';
import { renderWithProviders } from '@/test/utils';
import { useConversationSettingsStore } from '@/stores/conversationSettingsStore';
import { useAppStore } from '@/stores/appStore';

const mocks = vi.hoisted(() => ({
  mutate: vi.fn(),
}));

vi.mock('../useConversationsQuery', () => ({
  useConversationsQuery: () => ({
    data: [
      { id: 'conv-1', title: 'First', agentName: 'agent-1', updatedAt: '2026-01-01T00:00:00Z' },
    ],
    isLoading: false,
    error: null,
  }),
}));

vi.mock('../useDeleteConversation', () => ({
  useDeleteConversation: () => ({ mutate: mocks.mutate }),
}));

describe('ConversationSidebar', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAppStore.setState({ selectedAgent: 'agent-1', activeConversationId: 'conv-1', showSidebar: true });
    useConversationSettingsStore.setState({ byConversationId: {} });
  });

  it('clears the deleted conversation\'s per-conversation settings', async () => {
    // Seed settings for the conversation that is about to be deleted.
    useConversationSettingsStore.getState().setSettings('conv-1', {
      deploymentName: 'gpt-x',
      temperature: 0.5,
      systemPromptOverride: null,
    });
    expect(useConversationSettingsStore.getState().byConversationId['conv-1']).toBeDefined();

    renderWithProviders(<ConversationSidebar />);

    await userEvent.click(screen.getByLabelText('Delete conversation First'));

    expect(mocks.mutate).toHaveBeenCalledWith('conv-1');
    // The settings entry for the deleted conversation must be gone (no leak).
    expect(useConversationSettingsStore.getState().byConversationId['conv-1']).toBeUndefined();
  });
});
