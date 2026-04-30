import { apiClient } from './client';

interface ClientLogEntry {
  sessionId: string;
  level: string;
  message: string;
  timestamp: string;
  url?: string;
  stack?: string;
}

const SESSION_ID = crypto.randomUUID();
let buffer: ClientLogEntry[] = [];
let flushTimer: ReturnType<typeof setTimeout> | null = null;

function flush() {
  if (buffer.length === 0) return;
  const entries = [...buffer];
  buffer = [];
  apiClient.post('/api/client-logs', entries).catch(() => {});
}

function enqueue(level: string, message: string, stack?: string) {
  buffer.push({
    sessionId: SESSION_ID,
    level,
    message,
    timestamp: new Date().toISOString(),
    url: window.location.href,
    stack,
  });
  if (!flushTimer) {
    flushTimer = setTimeout(() => {
      flushTimer = null;
      flush();
    }, 2000);
  }
}

export function initClientLogger() {
  window.addEventListener('error', (event) => {
    enqueue('error', event.message, event.error?.stack);
  });

  window.addEventListener('unhandledrejection', (event) => {
    const reason = event.reason;
    enqueue('error', reason?.message ?? String(reason), reason?.stack);
  });

  const origError = console.error;
  console.error = (...args: unknown[]) => {
    origError.apply(console, args);
    enqueue('error', args.map(String).join(' '));
  };

  const origWarn = console.warn;
  console.warn = (...args: unknown[]) => {
    origWarn.apply(console, args);
    enqueue('warn', args.map(String).join(' '));
  };
}
