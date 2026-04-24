import { describe, it, expect, beforeEach } from 'vitest';
import { useTimeRangeStore } from './timeRangeStore';

describe('timeRangeStore', () => {
  beforeEach(() => {
    useTimeRangeStore.setState({ preset: '1h', customStart: null, customEnd: null, refreshIntervalSeconds: 30 });
  });

  it('defaults to 1h preset', () => {
    expect(useTimeRangeStore.getState().preset).toBe('1h');
  });

  it('setPreset updates preset and clears custom range', () => {
    useTimeRangeStore.getState().setCustomRange('2024-01-01', '2024-01-02');
    useTimeRangeStore.getState().setPreset('6h');
    const state = useTimeRangeStore.getState();
    expect(state.preset).toBe('6h');
    expect(state.customStart).toBeNull();
    expect(state.customEnd).toBeNull();
  });

  it('setCustomRange sets preset to custom', () => {
    useTimeRangeStore.getState().setCustomRange('2024-01-01T00:00:00Z', '2024-01-02T00:00:00Z');
    const state = useTimeRangeStore.getState();
    expect(state.preset).toBe('custom');
    expect(state.customStart).toBe('2024-01-01T00:00:00Z');
    expect(state.customEnd).toBe('2024-01-02T00:00:00Z');
  });

  it('setRefreshInterval updates interval', () => {
    useTimeRangeStore.getState().setRefreshInterval(60);
    expect(useTimeRangeStore.getState().refreshIntervalSeconds).toBe(60);
  });

  it('getRange returns correct range for 1h preset', () => {
    const { start, end, step } = useTimeRangeStore.getState().getRange();
    const startNum = parseInt(start);
    const endNum = parseInt(end);
    expect(endNum - startNum).toBe(3600);
    expect(step).toBe('15s');
  });

  it('getRange returns correct range for 24h preset', () => {
    useTimeRangeStore.getState().setPreset('24h');
    const { start, end, step } = useTimeRangeStore.getState().getRange();
    const startNum = parseInt(start);
    const endNum = parseInt(end);
    expect(endNum - startNum).toBe(86400);
    expect(step).toBe('5m');
  });

  it('getRange returns correct range for 7d preset', () => {
    useTimeRangeStore.getState().setPreset('7d');
    const { start, end, step } = useTimeRangeStore.getState().getRange();
    const startNum = parseInt(start);
    const endNum = parseInt(end);
    expect(endNum - startNum).toBe(604800);
    expect(step).toBe('30m');
  });

  it('getRange returns correct range for custom range', () => {
    useTimeRangeStore.getState().setCustomRange('2024-01-01T00:00:00Z', '2024-01-01T01:00:00Z');
    const { start, end, step } = useTimeRangeStore.getState().getRange();
    expect(parseInt(end) - parseInt(start)).toBe(3600);
    expect(step).toBe('15s');
  });

  it('getRange returns 5m step for custom range > 1h', () => {
    useTimeRangeStore.getState().setCustomRange('2024-01-01T00:00:00Z', '2024-01-01T12:00:00Z');
    const { step } = useTimeRangeStore.getState().getRange();
    expect(step).toBe('5m');
  });

  it('getRange returns 30m step for custom range > 24h', () => {
    useTimeRangeStore.getState().setCustomRange('2024-01-01T00:00:00Z', '2024-01-05T00:00:00Z');
    const { step } = useTimeRangeStore.getState().getRange();
    expect(step).toBe('30m');
  });
});
