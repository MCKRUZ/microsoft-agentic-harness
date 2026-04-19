// Captures browser console events, window errors, and unhandled rejections,
// batches them, and POSTs to /api/client-logs so the Serilog ndjson sink on
// AgentHub captures front- and back-end logs in one stream. Enables cross-tier
// grep without needing a separate browser log source.

type Level = 'debug' | 'info' | 'warn' | 'error';

interface LogEntry {
  sessionId: string;
  level: Level;
  message: string;
  timestamp: string;
  url?: string;
  stack?: string;
}

const ENDPOINT = '/api/client-logs';
const MAX_BATCH = 50;
const FLUSH_INTERVAL_MS = 5000;
const MAX_MESSAGE = 8_000;

const sessionId = crypto.randomUUID();
const buffer: LogEntry[] = [];
let installed = false;

function formatArg(a: unknown): string {
  if (typeof a === 'string') return a;
  if (a instanceof Error) return `${a.message}\n${a.stack ?? ''}`;
  try { return JSON.stringify(a); } catch { return String(a); }
}

function enqueue(level: Level, args: unknown[], stack?: string): void {
  const raw = args.map(formatArg).join(' ');
  const message = raw.length > MAX_MESSAGE ? `${raw.slice(0, MAX_MESSAGE)}…[truncated]` : raw;
  buffer.push({
    sessionId,
    level,
    message,
    timestamp: new Date().toISOString(),
    url: window.location.href,
    stack,
  });
  if (buffer.length >= MAX_BATCH) void flush();
}

async function flush(): Promise<void> {
  if (buffer.length === 0) return;
  const batch = buffer.splice(0, buffer.length);
  const body = JSON.stringify(batch);

  // sendBeacon wins during unload: guaranteed delivery even as the page is tearing down.
  if (document.visibilityState === 'hidden' && 'sendBeacon' in navigator) {
    try {
      navigator.sendBeacon(ENDPOINT, new Blob([body], { type: 'application/json' }));
      return;
    } catch {
      // fall through to fetch
    }
  }

  try {
    await fetch(ENDPOINT, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body,
      keepalive: true,
      credentials: 'include',
    });
  } catch {
    // Swallow — logging failures must never cascade into the app.
  }
}

/**
 * Installs the browser log pipeline. Idempotent. Call once before React renders
 * so the wrappers are in place before any component can emit console output.
 */
export function installBrowserLogger(): void {
  if (installed) return;
  installed = true;

  const levels: Level[] = ['debug', 'info', 'warn', 'error'];
  const rawConsole = console as unknown as Record<Level, (...a: unknown[]) => void>;

  for (const level of levels) {
    const original = rawConsole[level];
    rawConsole[level] = (...args: unknown[]) => {
      try { enqueue(level, args); } catch { /* never break logging */ }
      original.apply(console, args);
    };
  }

  window.addEventListener('error', (ev) => {
    const stack = ev.error instanceof Error ? ev.error.stack : undefined;
    enqueue('error', [ev.message, `at ${ev.filename}:${ev.lineno}:${ev.colno}`], stack);
  });

  window.addEventListener('unhandledrejection', (ev) => {
    const reason = ev.reason;
    const stack = reason instanceof Error ? reason.stack : undefined;
    const message = reason instanceof Error ? reason.message : String(reason);
    enqueue('error', [`Unhandled rejection: ${message}`], stack);
  });

  setInterval(() => { void flush(); }, FLUSH_INTERVAL_MS);
  window.addEventListener('pagehide', () => { void flush(); });
  window.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') void flush();
  });
}
