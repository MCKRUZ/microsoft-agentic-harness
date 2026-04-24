import { cn } from '@/lib/utils';

interface RoleBadgeProps {
  role: string;
  className?: string;
}

const roleStyles: Record<string, string> = {
  user: 'bg-blue-500/15 text-blue-400',
  assistant: 'bg-emerald-500/15 text-emerald-400',
  tool: 'bg-purple-500/15 text-purple-400',
};

export function RoleBadge({ role, className }: RoleBadgeProps) {
  const style = roleStyles[role.toLowerCase()] ?? 'bg-muted text-muted-foreground';
  return (
    <span className={cn('inline-block rounded-full px-2 py-0.5 text-xs font-medium capitalize', style, className)}>
      {role}
    </span>
  );
}
