import { ScrollArea } from '@/components/ui/scroll-area';
import { Separator } from '@/components/ui/separator';

interface PanelViewProps {
  label: string;
  headerExtra?: React.ReactNode;
  actions?: React.ReactNode;
  children: React.ReactNode;
}

export function PanelView({ label, headerExtra, actions, children }: PanelViewProps) {
  return (
    <main role="main" aria-label={label} className="flex flex-col flex-1 min-w-0 h-full overflow-hidden bg-background">
      <div className="flex items-center justify-between h-12 min-h-12 px-5 shrink-0">
        <div className="flex items-center gap-2">
          <h1 className="text-sm font-semibold tracking-tight text-foreground">{label}</h1>
        </div>
        {actions && <div className="flex items-center gap-1.5">{actions}</div>}
      </div>
      <Separator className="opacity-50" />
      {headerExtra}
      <ScrollArea className="flex-1 min-h-0">
        <div className="p-5">
          {children}
        </div>
      </ScrollArea>
    </main>
  );
}
