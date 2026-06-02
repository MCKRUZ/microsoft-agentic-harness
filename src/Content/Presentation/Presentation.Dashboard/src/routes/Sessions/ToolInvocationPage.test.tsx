import { describe, it, expect } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import { renderRoutedPage } from '@/test/helpers/renderPage';
import ToolInvocationPage from './ToolInvocationPage';

const sessionId = '11111111-1111-1111-1111-111111111111';
const invocationId = '22222222-2222-2222-2222-222222222222';

describe('ToolInvocationPage', () => {
  it('renders metadata, args, and stdout from the deep-link endpoint', async () => {
    renderRoutedPage(ToolInvocationPage, {
      route: `/sessions/${sessionId}/tools/${invocationId}`,
      path: '/sessions/:sessionId/tools/:invocationId',
    });

    // Header reflects toolName + source/timestamp subtitle.
    await waitFor(
      () => {
        expect(screen.getByText('ReadFile')).toBeInTheDocument();
      },
      { timeout: 3000 },
    );

    expect(screen.getByText(/keyed_di/)).toBeInTheDocument();

    // Status pill + metric grid values from the mocked response.
    expect(screen.getByText('success')).toBeInTheDocument();
    expect(screen.getByText('42ms')).toBeInTheDocument();
    expect(screen.getByText('128')).toBeInTheDocument();
    expect(screen.getByText('call-abc123')).toBeInTheDocument();

    // Args + stdout rendered into the <pre data-testid="…"> blocks.
    const argsEl = screen.getByTestId('tool-invocation-args');
    expect(argsEl.textContent).toContain('"path"');
    expect(argsEl.textContent).toContain('src/app.tsx');

    const stdoutEl = screen.getByTestId('tool-invocation-stdout');
    expect(stdoutEl.textContent).toContain('export default function App()');

    // Back link points to the parent session detail.
    const backLink = screen.getByRole('link', { name: /Back to session/ });
    expect(backLink).toHaveAttribute('href', `/sessions/${sessionId}`);
  });

  it('renders a not-found empty state for a missing invocation', async () => {
    renderRoutedPage(ToolInvocationPage, {
      route: `/sessions/${sessionId}/tools/00000000-0000-0000-0000-000000000404`,
      path: '/sessions/:sessionId/tools/:invocationId',
    });

    await waitFor(
      () => {
        expect(
          screen.getByText(/Failed to load invocation|Invocation not found/),
        ).toBeInTheDocument();
      },
      { timeout: 3000 },
    );
  });
});
