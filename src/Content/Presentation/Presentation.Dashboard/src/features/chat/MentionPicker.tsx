import { useEffect, useRef } from 'react';

export interface MentionItem {
  name: string;
  description?: string;
}

interface MentionPickerProps {
  items: MentionItem[];
  activeIndex: number;
  onSelect: (item: MentionItem) => void;
  onHover: (index: number) => void;
  trigger: '@' | '/';
  loading?: boolean;
}

export function MentionPicker({ items, activeIndex, onSelect, onHover, trigger, loading }: MentionPickerProps) {
  const listRef = useRef<HTMLUListElement>(null);

  useEffect(() => {
    const el = listRef.current?.children[activeIndex] as HTMLElement | undefined;
    el?.scrollIntoView({ block: 'nearest' });
  }, [activeIndex]);

  const label = trigger === '@' ? 'prompts' : 'tools';

  return (
    <div
      role="listbox"
      aria-label={`Available ${label}`}
      className="absolute bottom-full mb-1 left-3 right-3 max-h-56 overflow-auto rounded border bg-popover text-popover-foreground shadow-md z-20"
    >
      <div className="px-2 py-1 text-[10px] uppercase tracking-wide text-muted-foreground border-b">
        {trigger === '@' ? 'Prompts' : 'Tools'}
      </div>
      {loading ? (
        <div className="px-2 py-2 text-xs text-muted-foreground">Loading...</div>
      ) : items.length === 0 ? (
        <div className="px-2 py-2 text-xs text-muted-foreground">No matches</div>
      ) : (
        <ul ref={listRef} className="py-1">
          {items.map((item, i) => (
            <li
              key={item.name}
              role="option"
              aria-selected={i === activeIndex}
              onMouseEnter={() => { onHover(i); }}
              onMouseDown={(e) => {
                e.preventDefault();
                onSelect(item);
              }}
              className={`cursor-pointer px-2 py-1 text-sm ${i === activeIndex ? 'bg-accent text-accent-foreground' : ''}`}
            >
              <div className="font-medium">{item.name}</div>
              {item.description && (
                <div className="text-xs text-muted-foreground truncate">{item.description}</div>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
