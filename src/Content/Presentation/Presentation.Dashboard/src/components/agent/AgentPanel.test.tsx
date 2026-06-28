import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';

const sendMessage = vi.fn(async () => {});
vi.mock('@/hooks/useDashboardAgent', () => ({
  useDashboardAgent: () => ({ sendMessage }),
}));

// Stub the recharts-backed chart components — jsdom has no layout, so render simple markers.
vi.mock('@/components/charts/TimeSeriesChart', () => ({
  TimeSeriesChart: () => <div data-testid="timeseries-chart" />,
}));
vi.mock('@/components/charts/BarChart', () => ({
  MetricBarChart: () => <div data-testid="bar-chart" />,
}));

import { AgentPanel } from './AgentPanel';
import { useChatStore } from '@/stores/chatStore';

describe('AgentPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useChatStore.setState({ open: false, threadId: null, messages: [], status: 'idle', error: null, toolActivity: null });
  });

  it('is not rendered when the store is closed', () => {
    render(<AgentPanel />);
    expect(screen.queryByTestId('agent-panel')).not.toBeInTheDocument();
  });

  it('renders the transcript when open', () => {
    useChatStore.setState({
      open: true,
      messages: [
        { id: 'u1', role: 'user', content: 'show spend' },
        { id: 'a1', role: 'assistant', content: 'On it.' },
      ],
    });
    render(<AgentPanel />);
    expect(screen.getByTestId('agent-panel')).toBeInTheDocument();
    expect(screen.getByText('show spend')).toBeInTheDocument();
    expect(screen.getByText('On it.')).toBeInTheDocument();
  });

  it('submits the input via the hook and clears the field', () => {
    useChatStore.setState({ open: true });
    render(<AgentPanel />);

    const input = screen.getByTestId('agent-panel-input') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'refresh the data' } });
    fireEvent.submit(input.closest('form')!);

    expect(sendMessage).toHaveBeenCalledWith('refresh the data');
    expect(input.value).toBe('');
  });

  it('disables send while a run is in progress', () => {
    useChatStore.setState({ open: true, status: 'running' });
    render(<AgentPanel />);
    expect(screen.getByTestId('agent-panel-send')).toBeDisabled();
  });

  it('renders an inline chart message with its title and a pie/bar chart', () => {
    useChatStore.setState({
      open: true,
      messages: [
        {
          id: 'c1',
          role: 'assistant',
          content: 'Rendered a pie chart.',
          chart: { title: 'Tokens by Model', chartType: 'pie', unit: 'tokens', series: [{ labels: {}, dataPoints: [{ timestamp: 1, value: '5' }] }] },
        },
      ],
    });
    render(<AgentPanel />);
    expect(screen.getByTestId('agent-message-chart')).toBeInTheDocument();
    expect(screen.getByText('Tokens by Model')).toBeInTheDocument();
    expect(screen.getByTestId('bar-chart')).toBeInTheDocument();
  });

  it('shows the error banner when the run failed', () => {
    useChatStore.setState({ open: true, status: 'error', error: 'Access denied.' });
    render(<AgentPanel />);
    expect(screen.getByTestId('agent-panel-error')).toHaveTextContent('Access denied.');
  });
});
