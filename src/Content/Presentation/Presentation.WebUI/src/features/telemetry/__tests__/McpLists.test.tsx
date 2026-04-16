import { describe, it, expect, vi } from 'vitest';

vi.mock('@/lib/authConfig', () => ({
  loginRequest: { scopes: ['api://test-api/access_as_user'] },
}));
import { screen } from '@testing-library/react';
import { renderWithProviders } from '@/test/utils';
import { ResourcesList } from '@/features/mcp/ResourcesList';
import { PromptsList } from '@/features/mcp/PromptsList';

describe('ResourcesList', () => {
  it('renders resource URI, name, and description from MSW mock', async () => {
    renderWithProviders(<ResourcesList />);
    await screen.findByText('Readme');
    expect(screen.getByText('file://docs/readme.md')).toBeInTheDocument();
    expect(screen.getByText('Project readme')).toBeInTheDocument();
  });
});

describe('PromptsList', () => {
  it('renders prompt name and description from MSW mock', async () => {
    renderWithProviders(<PromptsList />);
    await screen.findByText('summarize');
    expect(screen.getByText('Summarize text')).toBeInTheDocument();
    expect(screen.getByText('Args: text')).toBeInTheDocument();
  });
});
