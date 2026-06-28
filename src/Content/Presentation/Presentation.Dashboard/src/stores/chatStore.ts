import { create } from 'zustand';
import type { MetricSeries } from '@/api/types';

/** Role of a message shown in the agent chat transcript. */
export type ChatRole = 'user' | 'assistant';

/** A chart the agent rendered inline, populated from the dashboard's existing metric data. */
export interface ChartSpec {
  /** Heading shown above the chart. */
  title: string;
  /** Which chart component to render: `timeseries`, `bar`, or `pie`. */
  chartType: string;
  /** Optional display unit (e.g. `tokens`, `usd`). */
  unit?: string;
  /** The metric series to plot, in the dashboard's standard shape. */
  series: MetricSeries[];
}

/**
 * A single message in the agent chat transcript. A message is plain text unless {@link chart} is set,
 * in which case the panel renders the chart inline and uses {@link content} as its caption/summary.
 */
export interface ChatMessage {
  id: string;
  role: ChatRole;
  content: string;
  chart?: ChartSpec;
}

/** Lifecycle of the current agent run, used to gate input and show status. */
export type ChatStatus = 'idle' | 'running' | 'error';

interface ChatState {
  /** Whether the agent panel is open. */
  open: boolean;
  /** The conversation thread id, created once per session and reused across turns. */
  threadId: string | null;
  /** The chat transcript, oldest first. */
  messages: ChatMessage[];
  /** Current run status. */
  status: ChatStatus;
  /** Last error message, when {@link status} is `error`. */
  error: string | null;
  /** A short, transient note describing the action the agent is performing (e.g. "navigate → /spend"). */
  toolActivity: string | null;

  setOpen: (open: boolean) => void;
  toggle: () => void;
  setThreadId: (id: string | null) => void;
  setStatus: (status: ChatStatus) => void;
  setError: (message: string | null) => void;
  setToolActivity: (activity: string | null) => void;
  /** Appends a new message to the transcript. */
  addMessage: (message: ChatMessage) => void;
  /** Appends a text delta to the message with the given id (no-op if it does not exist). */
  appendToMessage: (id: string, delta: string) => void;
  /** Clears the transcript and run state. Does not change {@link open} or {@link threadId}. */
  reset: () => void;
}

export const useChatStore = create<ChatState>((set) => ({
  open: false,
  threadId: null,
  messages: [],
  status: 'idle',
  error: null,
  toolActivity: null,

  setOpen: (open) => set({ open }),
  toggle: () => set((state) => ({ open: !state.open })),
  setThreadId: (threadId) => set({ threadId }),
  setStatus: (status) => set({ status }),
  setError: (error) => set({ error }),
  setToolActivity: (toolActivity) => set({ toolActivity }),

  addMessage: (message) => set((state) => ({ messages: [...state.messages, message] })),

  appendToMessage: (id, delta) =>
    set((state) => ({
      messages: state.messages.map((m) =>
        m.id === id ? { ...m, content: m.content + delta } : m,
      ),
    })),

  reset: () => set({ messages: [], status: 'idle', error: null, toolActivity: null }),
}));
