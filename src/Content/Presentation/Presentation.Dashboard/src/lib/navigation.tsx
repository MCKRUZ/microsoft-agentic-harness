import { MessageCircle, Bot, Wrench, Database, FileText } from 'lucide-react';

const ICON_SIZE = 18;

export interface NavItem {
  path: string;
  label: string;
  icon: React.ReactNode;
  keywords: string[];
}

export const NAV_ITEMS: readonly NavItem[] = [
  { path: '/chat',       label: 'Chat',          icon: <MessageCircle size={ICON_SIZE} />, keywords: ['chats', 'conversations'] },
  { path: '/agents',     label: 'Agents',        icon: <Bot size={ICON_SIZE} />,           keywords: ['agents', 'bot'] },
  { path: '/tools',      label: 'Tools',         icon: <Wrench size={ICON_SIZE} />,        keywords: ['tools', 'mcp'] },
  { path: '/resources',  label: 'Resources',     icon: <Database size={ICON_SIZE} />,      keywords: ['resources', 'mcp'] },
  { path: '/prompts',    label: 'Prompts',       icon: <FileText size={ICON_SIZE} />,      keywords: ['prompts', 'mcp'] },
];
