import { useRef, useCallback, useEffect, type ReactNode } from 'react';

interface SplitPanelProps {
  left: ReactNode;
  right: ReactNode;
}

export function SplitPanel({ left, right }: SplitPanelProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const dragCleanupRef = useRef<(() => void) | null>(null);

  // Clean up any in-progress drag if the component unmounts mid-drag (StrictMode safe)
  useEffect(() => {
    return () => { dragCleanupRef.current?.(); };
  }, []);

  const setSplitFraction = useCallback((fraction: number) => {
    const clamped = Math.max(0.2, Math.min(0.8, fraction));
    containerRef.current?.style.setProperty('--split-pos', `${clamped * 100}%`);
  }, []);

  const handleDividerMouseDown = useCallback((e: React.MouseEvent) => {
    const container = containerRef.current;
    if (!container) return;
    e.preventDefault();

    const onMouseMove = (moveEvent: MouseEvent) => {
      const rect = container.getBoundingClientRect();
      setSplitFraction((moveEvent.clientX - rect.left) / rect.width);
    };

    const cleanup = () => {
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
      dragCleanupRef.current = null;
    };

    const onMouseUp = cleanup;

    dragCleanupRef.current = cleanup;
    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
  }, [setSplitFraction]);

  const handleDividerKeyDown = useCallback((e: React.KeyboardEvent) => {
    const container = containerRef.current;
    if (!container) return;
    const current = parseFloat(
      container.style.getPropertyValue('--split-pos') || '40'
    ) / 100;
    const step = e.shiftKey ? 0.1 : 0.02;
    if (e.key === 'ArrowLeft') { e.preventDefault(); setSplitFraction(current - step); }
    if (e.key === 'ArrowRight') { e.preventDefault(); setSplitFraction(current + step); }
  }, [setSplitFraction]);

  return (
    <div
      ref={containerRef}
      style={{
        display: 'grid',
        gridTemplateColumns: 'var(--split-pos, 40%) 4px 1fr',
        height: '100%',
      }}
    >
      <main role="main" aria-label="Chat panel" className="overflow-hidden">
        {left}
      </main>
      <div
        role="separator"
        aria-label="Resize panels"
        tabIndex={0}
        style={{ cursor: 'col-resize' }}
        className="bg-border focus:outline-none focus:ring-2 focus:ring-ring"
        onMouseDown={handleDividerMouseDown}
        onKeyDown={handleDividerKeyDown}
      />
      <div aria-label="Details panel" className="overflow-hidden">
        {right}
      </div>
    </div>
  );
}
