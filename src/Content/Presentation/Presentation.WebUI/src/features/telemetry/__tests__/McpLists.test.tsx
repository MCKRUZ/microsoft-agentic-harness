import { describe, it, expect, vi, beforeAll, afterEach, afterAll } from 'vitest';

vi.mock('@/lib/authConfig', () => ({
  loginRequest: { scopes: ['api://test-api/access_as_user'] },
}));
import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { renderWithProviders } from '@/test/utils';
import { ResourcesList } from '@/features/mcp/ResourcesList';
import { PromptsList } from '@/features/mcp/PromptsList';

const server = setupServer(
  http.get('http://localhost/api/mcp/resources', () =>
    HttpResponse.json([
      { uri: 'file://docs/readme.md', name: 'Readme', description: 'Project readme' },
    ]),
  ),
  http.get('http://localhost/api/mcp/prompts', () =>
    HttpResponse.json([
      { name: 'summarize', description: 'Summarize text', arguments: [{ name: 'text' }] },
    ]),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

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
  });
});
