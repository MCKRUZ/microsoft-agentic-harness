import { create } from 'zustand';
import type { TelemetryEvent } from '@/api/types';

const MAX_BUFFER_SIZE = 500;

interface TelemetryState {
  events: TelemetryEvent[];
  connected: boolean;
  push: (event: TelemetryEvent) => void;
  setConnected: (connected: boolean) => void;
  clear: () => void;
}

export const useTelemetryStore = create<TelemetryState>((set) => ({
  events: [],
  connected: false,

  push: (event) =>
    set((state) => ({
      events: [...state.events, event].slice(-MAX_BUFFER_SIZE),
    })),

  setConnected: (connected) => set({ connected }),
  clear: () => set({ events: [] }),
}));
