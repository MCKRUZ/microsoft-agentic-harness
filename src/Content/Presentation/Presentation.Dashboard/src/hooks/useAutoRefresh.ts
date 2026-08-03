import { useEffect, useRef } from 'react';

export function useAutoRefresh(callback: () => void, intervalMs: number, enabled = true): void {
  const savedCallback = useRef(callback);

  // Assigning to a ref during render is a side effect in the render phase: React may discard
  // or re-run a render, so the write can be lost or applied twice, and under concurrent
  // rendering an interleaved render can leave the ref pointing at a callback from a tree that
  // was never committed. Syncing after commit is the only ordering that always matches what
  // is on screen — and the interval below fires asynchronously, so it never observes the ref
  // before this has run.
  useEffect(() => {
    savedCallback.current = callback;
  }, [callback]);

  useEffect(() => {
    if (!enabled || intervalMs <= 0) return;
    const id = setInterval(() => savedCallback.current(), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs, enabled]);
}
