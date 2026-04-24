import { create } from 'zustand';

export type TimeRangePreset = '1h' | '6h' | '24h' | '7d' | 'custom';

interface TimeRangeState {
  preset: TimeRangePreset;
  customStart: string | null;
  customEnd: string | null;
  refreshIntervalSeconds: number;
  setPreset: (preset: TimeRangePreset) => void;
  setCustomRange: (start: string, end: string) => void;
  setRefreshInterval: (seconds: number) => void;
  getRange: () => { start: string; end: string; step: string };
}

const presetToSeconds: Record<Exclude<TimeRangePreset, 'custom'>, number> = {
  '1h': 3600,
  '6h': 21600,
  '24h': 86400,
  '7d': 604800,
};

const presetToStep: Record<Exclude<TimeRangePreset, 'custom'>, string> = {
  '1h': '15s',
  '6h': '1m',
  '24h': '5m',
  '7d': '30m',
};

export const useTimeRangeStore = create<TimeRangeState>((set, get) => ({
  preset: '1h',
  customStart: null,
  customEnd: null,
  refreshIntervalSeconds: 30,

  setPreset: (preset) => set({ preset, customStart: null, customEnd: null }),

  setCustomRange: (start, end) => set({ preset: 'custom', customStart: start, customEnd: end }),

  setRefreshInterval: (seconds) => set({ refreshIntervalSeconds: seconds }),

  getRange: () => {
    const state = get();
    if (state.preset === 'custom' && state.customStart && state.customEnd) {
      const startSec = Math.floor(new Date(state.customStart).getTime() / 1000);
      const endSec = Math.floor(new Date(state.customEnd).getTime() / 1000);
      const durationSec = endSec - startSec;
      const step = durationSec <= 3600 ? '15s' : durationSec <= 86400 ? '5m' : '30m';
      return { start: String(startSec), end: String(endSec), step };
    }

    const durationSec = presetToSeconds[state.preset as Exclude<TimeRangePreset, 'custom'>] ?? 3600;
    const step = presetToStep[state.preset as Exclude<TimeRangePreset, 'custom'>] ?? '15s';
    const end = Math.floor(Date.now() / 1000);
    const start = end - durationSec;
    return { start: String(start), end: String(end), step };
  },
}));
