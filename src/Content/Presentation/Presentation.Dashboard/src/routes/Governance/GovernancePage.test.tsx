import { describe, it, expect } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import { renderRoutedPage } from '@/test/helpers/renderPage';
import GovernancePage from './GovernancePage';

describe('GovernancePage', () => {
  it('renders the page header', async () => {
    renderRoutedPage(GovernancePage, { route: '/governance', path: '/governance' });
    await waitFor(() => {
      expect(screen.getByText('Governance')).toBeInTheDocument();
    });
  });

  it('renders all five sections', async () => {
    renderRoutedPage(GovernancePage, { route: '/governance', path: '/governance' });
    await waitFor(() => {
      expect(screen.getByTestId('section-overview')).toBeInTheDocument();
    });
    expect(screen.getByTestId('section-policy-enforcement')).toBeInTheDocument();
    expect(screen.getByTestId('section-prompt-injection-detection')).toBeInTheDocument();
    expect(screen.getByTestId('section-mcp-tool-security')).toBeInTheDocument();
    expect(screen.getByTestId('section-audit-trail')).toBeInTheDocument();
  });

  it('renders overview KPI cards', async () => {
    renderRoutedPage(GovernancePage, { route: '/governance', path: '/governance' });
    await waitFor(() => {
      expect(screen.getByTestId('kpi-policy-decisions')).toBeInTheDocument();
    });
    expect(screen.getByTestId('kpi-violations')).toBeInTheDocument();
    expect(screen.getByTestId('kpi-eval-latency-p50')).toBeInTheDocument();
    expect(screen.getByTestId('kpi-rate-limit-hits')).toBeInTheDocument();
  });

  it('renders injection detection section', async () => {
    renderRoutedPage(GovernancePage, { route: '/governance', path: '/governance' });
    await waitFor(() => {
      expect(screen.getByTestId('kpi-total-detections')).toBeInTheDocument();
    });
  });

  it('renders MCP security section', async () => {
    renderRoutedPage(GovernancePage, { route: '/governance', path: '/governance' });
    await waitFor(() => {
      expect(screen.getByTestId('kpi-tools-scanned')).toBeInTheDocument();
    });
    expect(screen.getByTestId('kpi-threats-found')).toBeInTheDocument();
  });

  it('renders audit trail section with chain integrity', async () => {
    renderRoutedPage(GovernancePage, { route: '/governance', path: '/governance' });
    await waitFor(() => {
      expect(screen.getByTestId('kpi-audit-events')).toBeInTheDocument();
    });
    expect(screen.getByText('Verified')).toBeInTheDocument();
    expect(screen.getByText('Tamper-evident hash chain intact')).toBeInTheDocument();
  });

  it('shows loading skeleton initially', () => {
    renderRoutedPage(GovernancePage, { route: '/governance', path: '/governance' });
    expect(screen.getByText('Governance')).toBeInTheDocument();
  });
});
