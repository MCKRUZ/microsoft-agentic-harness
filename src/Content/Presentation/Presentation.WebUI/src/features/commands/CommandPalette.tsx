import { useEffect, useMemo, useRef, useState } from 'react';
import { Search } from 'lucide-react';

export interface CommandItem {
  id: string;
  label: string;
  hint?: string;
  group?: string;
  run: () => void;
  keywords?: string[];
}

interface CommandPaletteProps {
  onClose: () => void;
  commands: CommandItem[];
}

function matches(item: CommandItem, query: string): boolean {
  if (!query) return true;
  const q = query.toLowerCase();
  if (item.label.toLowerCase().includes(q)) return true;
  if (item.hint?.toLowerCase().includes(q)) return true;
  if (item.group?.toLowerCase().includes(q)) return true;
  return (item.keywords ?? []).some((k) => k.toLowerCase().includes(q));
}

// Rendered only while open (DashboardLayout mounts it conditionally), so there is no `open`
// prop and no reset effect: mounting IS the reset. The previous form stayed mounted and
// cleared its own query/highlight from an effect, which is state adjustment inside an effect
// and cost an extra render pass on every open.
export function CommandPalette({ onClose, commands }: CommandPaletteProps) {
  const [query, setQuery] = useState('');
  const [activeIndex, setActiveIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  // Focus is a genuine post-commit DOM effect, not state adjustment. rAF defers to after the
  // dialog is painted, which is what makes the caret land reliably on first open.
  useEffect(() => {
    requestAnimationFrame(() => { inputRef.current?.focus(); });
  }, []);

  const filtered = useMemo(
    () => commands.filter((c) => matches(c, query)),
    [commands, query],
  );

  // Clamped during render rather than corrected by an effect. The effect form committed one
  // render in which activeIndex pointed past the end of `filtered` — so for a frame the
  // highlighted row was simply absent, and Enter in that frame resolved against an
  // out-of-range index. Deriving makes the out-of-range state unrepresentable instead of
  // merely short-lived.
  const activeIdx = activeIndex >= filtered.length ? 0 : activeIndex;

  useEffect(() => {
    const el = listRef.current?.children[activeIdx] as HTMLElement | undefined;
    el?.scrollIntoView({ block: 'nearest' });
  }, [activeIdx]);

  const runItem = (item: CommandItem): void => {
    item.run();
    onClose();
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>): void => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setActiveIndex((i) => (filtered.length === 0 ? 0 : (i + 1) % filtered.length));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActiveIndex((i) => (filtered.length === 0 ? 0 : (i - 1 + filtered.length) % filtered.length));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const item = filtered[activeIdx];
      if (item) runItem(item);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
    }
  };

  // Group items by `group` for the display sections.
  const groups = new Map<string, CommandItem[]>();
  filtered.forEach((c) => {
    const g = c.group ?? 'Actions';
    if (!groups.has(g)) groups.set(g, []);
    groups.get(g)!.push(c);
  });

  let flatIndex = -1;

  return (
    <>
      <div className="fixed inset-0 bg-black/40 z-40" onClick={onClose} aria-hidden="true" />
      <div
        role="dialog"
        aria-label="Command palette"
        aria-modal="true"
        className="fixed left-1/2 top-[15vh] -translate-x-1/2 z-50 w-[min(560px,90vw)] bg-popover text-popover-foreground border rounded-lg shadow-2xl overflow-hidden"
      >
        <div className="flex items-center gap-2 px-3 py-2 border-b">
          <Search size={16} className="text-muted-foreground shrink-0" />
          <input
            ref={inputRef}
            type="text"
            value={query}
            onChange={(e) => { setQuery(e.target.value); setActiveIndex(0); }}
            onKeyDown={handleKeyDown}
            placeholder="Type a command..."
            aria-label="Command search"
            className="flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
          />
          <kbd className="text-[10px] uppercase tracking-wide text-muted-foreground border rounded px-1.5 py-0.5">Esc</kbd>
        </div>
        <ul ref={listRef} role="listbox" className="max-h-[50vh] overflow-auto py-1">
          {filtered.length === 0 ? (
            <li className="px-3 py-6 text-sm text-muted-foreground text-center">No commands match.</li>
          ) : (
            Array.from(groups.entries()).map(([group, items]) => (
              <div key={group}>
                <div className="px-3 pt-2 pb-1 text-[10px] uppercase tracking-wide text-muted-foreground">
                  {group}
                </div>
                {items.map((item) => {
                  flatIndex += 1;
                  const isActive = flatIndex === activeIdx;
                  const myIndex = flatIndex;
                  return (
                    <li
                      key={item.id}
                      role="option"
                      aria-selected={isActive}
                      onMouseEnter={() => { setActiveIndex(myIndex); }}
                      onMouseDown={(e) => {
                        e.preventDefault();
                        runItem(item);
                      }}
                      className={`flex items-center justify-between gap-2 px-3 py-2 text-sm cursor-pointer ${
                        isActive ? 'bg-accent text-accent-foreground' : ''
                      }`}
                    >
                      <span className="truncate">{item.label}</span>
                      {item.hint && (
                        <span className="text-xs text-muted-foreground truncate">{item.hint}</span>
                      )}
                    </li>
                  );
                })}
              </div>
            ))
          )}
        </ul>
      </div>
    </>
  );
}
