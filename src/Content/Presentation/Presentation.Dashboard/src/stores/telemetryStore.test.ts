import { describe, it, expect, beforeEach } from 'vitest';
import { useTelemetryStore } from './telemetryStore';
import type { TelemetryEvent } from '@/api/types';

function makeEvent(type: TelemetryEvent['type'] = 'MetricsUpdate'): TelemetryEvent {
  return { type, timestamp: Date.now(), data: { metric: 'test', value: '1' } };
}

describe('telemetryStore', () => {
  beforeEach(() => {
    useTelemetryStore.setState({ events: [], connected: false });
  });

  it('starts with empty events and disconnected', () => {
    const state = useTelemetryStore.getState();
    expect(state.events).toHaveLength(0);
    expect(state.connected).toBe(false);
  });

  it('push adds events', () => {
    useTelemetryStore.getState().push(makeEvent());
    useTelemetryStore.getState().push(makeEvent('TokenReceived'));
    expect(useTelemetryStore.getState().events).toHaveLength(2);
  });

  it('push enforces ring buffer max size of 500', () => {
    const store = useTelemetryStore.getState();
    for (let i = 0; i < 510; i++) {
      store.push(makeEvent());
    }
    expect(useTelemetryStore.getState().events).toHaveLength(500);
  });

  it('push preserves most recent events when buffer overflows', () => {
    const store = useTelemetryStore.getState();
    for (let i = 0; i < 502; i++) {
      store.push({ type: 'MetricsUpdate', timestamp: i, data: { index: i } });
    }
    const events = useTelemetryStore.getState().events;
    expect(events[0]!.timestamp).toBe(2);
    expect(events[events.length - 1]!.timestamp).toBe(501);
  });

  it('setConnected updates connected state', () => {
    useTelemetryStore.getState().setConnected(true);
    expect(useTelemetryStore.getState().connected).toBe(true);
  });

  it('clear empties events array', () => {
    useTelemetryStore.getState().push(makeEvent());
    useTelemetryStore.getState().push(makeEvent());
    useTelemetryStore.getState().clear();
    expect(useTelemetryStore.getState().events).toHaveLength(0);
  });
});
