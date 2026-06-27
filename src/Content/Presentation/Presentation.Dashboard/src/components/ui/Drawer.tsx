import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import type { ReactNode } from 'react';

interface DrawerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  children: ReactNode;
  side?: 'left' | 'right';
  widthClass?: string;
}

export function Drawer({
  open,
  onOpenChange,
  title,
  children,
  side = 'left',
  widthClass = 'w-80',
}: DrawerProps) {
  const sideClass = side === 'left' ? 'left-0' : 'right-0';
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/40 z-40 data-[state=open]:animate-in data-[state=closed]:animate-out" />
        <Dialog.Content
          className={`fixed top-0 bottom-0 ${sideClass} ${widthClass} bg-background border-r z-50 shadow-xl flex flex-col focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out`}
        >
          <div className="flex items-center justify-between px-4 h-14 border-b shrink-0">
            <Dialog.Title className="text-sm font-semibold">{title}</Dialog.Title>
            <Dialog.Close
              aria-label="Close"
              className="p-1 rounded hover:bg-accent text-muted-foreground"
            >
              <X size={16} />
            </Dialog.Close>
          </div>
          <div className="flex-1 min-h-0 overflow-auto">{children}</div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
