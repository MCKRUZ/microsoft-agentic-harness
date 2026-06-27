import { useCallback, useMemo, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { ChevronLeft, ChevronRight, X } from 'lucide-react';
import { cn } from '@/lib/utils';
import { CATEGORY_LABEL, type CategoryKey } from '@/lib/categories';
import { CategorySwatch } from './CategorySwatch';

export type DrawerLang = 'markdown' | 'json' | 'text';
export type DrawerRole = 'user' | 'assistant' | 'tool';

interface ContextDrawerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Which context category this artifact belongs to — drives the colored mark. */
  category: CategoryKey;
  /** Display name (e.g. "rules/testing"). */
  name: string;
  /** Source path, rendered in mono under the name. */
  path: string;
  /** Optional role banner — only shown for message drawers. */
  role?: DrawerRole;
  /** The full body to render. Line-numbered. */
  body: string;
  /** Affects lightweight syntax styling. Defaults to 'text'. */
  lang?: DrawerLang;
  onPrev?: () => void;
  onNext?: () => void;
  prevLabel?: string;
  nextLabel?: string;
}

const ROLE_LABEL: Record<DrawerRole, string> = {
  user: 'User message',
  assistant: 'Assistant message',
  tool: 'Tool result',
};

/**
 * Right-side drawer for drilling into any loaded artifact (file or message).
 * Sticky header (category mark + name + path), line-numbered body, sticky
 * footer (prev/next + keyboard hints). Esc closes; ← / → walk through the
 * current category. See foresight-dashboard-spec.md §4.2.
 *
 * Built on Radix Dialog — focus trap, scroll lock, and overlay come for free.
 */
export function ContextDrawer({
  open,
  onOpenChange,
  category,
  name,
  path,
  role,
  body,
  lang = 'text',
  onPrev,
  onNext,
  prevLabel,
  nextLabel,
}: ContextDrawerProps) {
  const lines = useMemo(() => body.split('\n'), [body]);

  /**
   * Arrow nav is scoped to Dialog.Content's focus trap (not `window`) so it
   * does not steal arrow keys from widgets the drawer overlays (Radix
   * Combobox, Select, code editors) or from caret movement inside <input>,
   * <textarea>, or contenteditable surfaces inside the drawer body itself.
   */
  const handleContentKeyDown = useCallback(
    (e: ReactKeyboardEvent<HTMLDivElement>) => {
      if (e.defaultPrevented) return;
      const target = e.target as HTMLElement | null;
      if (
        target &&
        (target.tagName === 'INPUT' ||
          target.tagName === 'TEXTAREA' ||
          target.isContentEditable)
      ) {
        return;
      }
      if (e.key === 'ArrowRight' && onNext) {
        e.preventDefault();
        e.stopPropagation();
        onNext();
      } else if (e.key === 'ArrowLeft' && onPrev) {
        e.preventDefault();
        e.stopPropagation();
        onPrev();
      }
    },
    [onPrev, onNext],
  );

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay
          data-testid="context-drawer-overlay"
          className="fixed inset-0 z-40 bg-foreground/30 backdrop-blur-sm data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=open]:fade-in-0 data-[state=closed]:fade-out-0"
        />
        <Dialog.Content
          data-testid="context-drawer"
          onKeyDown={handleContentKeyDown}
          className="fixed right-0 top-0 z-50 h-full w-full max-w-2xl bg-card border-l border-border shadow-2xl flex flex-col focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=open]:slide-in-from-right data-[state=closed]:slide-out-to-right"
        >
          <header className="sticky top-0 z-10 flex items-start gap-3 border-b border-border bg-card px-5 py-4">
            <CategorySwatch category={category} size="sm" className="mt-1" />
            <div className="flex-1 min-w-0">
              <Dialog.Title className="text-sm font-semibold text-foreground truncate">
                {name}
              </Dialog.Title>
              <Dialog.Description className="font-mono text-xs text-muted-foreground truncate">
                {path}
              </Dialog.Description>
              <div className="mt-1 text-[10px] font-medium uppercase tracking-wider text-muted-foreground">
                {CATEGORY_LABEL[category]}
              </div>
            </div>
            <Dialog.Close
              data-testid="context-drawer-close"
              className="rounded-md p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
              aria-label="Close drawer"
            >
              <X className="h-4 w-4" />
            </Dialog.Close>
          </header>

          {role && (
            <div
              data-testid="context-drawer-role-banner"
              data-role={role}
              className="border-b border-border bg-muted px-5 py-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground"
            >
              {ROLE_LABEL[role]}
            </div>
          )}

          <div
            data-testid="context-drawer-body"
            data-lang={lang}
            className="flex-1 overflow-auto px-5 py-4 font-mono text-xs leading-relaxed"
          >
            <pre className="m-0 grid grid-cols-[auto_1fr] gap-x-3">
              {lines.map((line, i) => (
                <div key={i} className="contents group">
                  <span
                    data-testid="context-drawer-line-number"
                    className="select-none text-right text-muted-foreground/60 group-hover:text-muted-foreground"
                  >
                    {i + 1}
                  </span>
                  <span className="whitespace-pre-wrap break-words text-foreground">
                    {renderLine(line, lang)}
                  </span>
                </div>
              ))}
            </pre>
          </div>

          <footer className="sticky bottom-0 flex items-center justify-between gap-3 border-t border-border bg-card px-5 py-3 text-[11px] text-muted-foreground">
            <div className="flex items-center gap-3">
              <kbd className="rounded border border-border bg-muted px-1.5 py-0.5 font-mono text-[10px]">Esc</kbd>
              <span>close</span>
              {(onPrev || onNext) && (
                <>
                  <kbd className="rounded border border-border bg-muted px-1.5 py-0.5 font-mono text-[10px]">← →</kbd>
                  <span>walk</span>
                </>
              )}
            </div>
            <div className="flex items-center gap-2">
              {onPrev && (
                <button
                  type="button"
                  data-testid="context-drawer-prev"
                  onClick={onPrev}
                  className="flex items-center gap-1 rounded-md border border-border px-2 py-1 text-foreground hover:bg-accent"
                >
                  <ChevronLeft className="h-3 w-3" />
                  <span className="truncate max-w-[10rem]">{prevLabel ?? 'Previous'}</span>
                </button>
              )}
              {onNext && (
                <button
                  type="button"
                  data-testid="context-drawer-next"
                  onClick={onNext}
                  className="flex items-center gap-1 rounded-md border border-border px-2 py-1 text-foreground hover:bg-accent"
                >
                  <span className="truncate max-w-[10rem]">{nextLabel ?? 'Next'}</span>
                  <ChevronRight className="h-3 w-3" />
                </button>
              )}
            </div>
          </footer>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

/**
 * Lightweight per-line syntax styling. All `lang`-driven decisions live here —
 * there is no parallel logic in the JSX above, so the contract is honored in
 * one place. Dependency-free; revisit only if real session content shows this
 * is not enough.
 *
 * markdown: bolds heading lines (`# `..`###### `).
 * json:     tints the leading `"key":` of any object line.
 * text:     passthrough.
 */
function renderLine(line: string, lang: DrawerLang) {
  if (lang === 'json') {
    const match = line.match(/^(\s*)"([^"]+)"(\s*):/);
    if (match) {
      return (
        <>
          {match[1]}
          <span className="text-cat-accent">"{match[2]}"</span>
          {match[3]}:{line.slice(match[0].length)}
        </>
      );
    }
  }
  if (lang === 'markdown' && /^#{1,6}\s/.test(line)) {
    return <strong className="font-semibold">{line}</strong>;
  }
  return line;
}
