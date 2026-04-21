import { useNavigate, useLocation } from 'react-router-dom';
import { NAV_ITEMS } from '@/lib/navigation';
import { Tooltip, TooltipTrigger, TooltipContent, TooltipProvider } from '@/components/ui/tooltip';
import { cn } from '@/lib/utils';

export function SidebarSwitcher() {
  const navigate = useNavigate();
  const { pathname } = useLocation();

  return (
    <TooltipProvider>
      <nav
        aria-label="Main navigation"
        className="flex flex-col items-center gap-0.5 border-r border-border/50 bg-background py-3 w-12 shrink-0"
      >
        {NAV_ITEMS.map((item) => {
          const active = pathname === item.path
            || (item.path !== '/' && pathname.startsWith(item.path + '/'));
          return (
            <Tooltip key={item.path}>
              <TooltipTrigger
                type="button"
                onClick={() => { navigate(item.path); }}
                aria-label={item.label}
                aria-current={active ? 'page' : undefined}
                className={cn(
                  'relative flex items-center justify-center rounded-md size-9 transition-all duration-150',
                  active
                    ? 'bg-accent text-foreground shadow-sm'
                    : 'text-muted-foreground hover:bg-accent/50 hover:text-foreground',
                )}
              >
                {active && (
                  <span className="absolute left-0 top-1/2 -translate-y-1/2 w-[2px] h-4 rounded-r bg-primary" />
                )}
                {item.icon}
              </TooltipTrigger>
              <TooltipContent side="right" sideOffset={8}>
                {item.label}
              </TooltipContent>
            </Tooltip>
          );
        })}
      </nav>
    </TooltipProvider>
  );
}
