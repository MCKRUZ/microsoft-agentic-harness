import { metricCatalog } from './metricCatalog';
import type { MetricCatalogEntry } from '@/api/types';

export interface PanelDefinition {
  id: string;
  metric: MetricCatalogEntry;
  size: 'kpi' | 'half' | 'full';
}

export interface RouteLayout {
  title: string;
  description: string;
  panels: PanelDefinition[];
}

function panel(catalogId: string, size: PanelDefinition['size']): PanelDefinition {
  const metric = metricCatalog[catalogId];
  if (!metric) throw new Error(`Unknown metric catalog entry: ${catalogId}`);
  return { id: catalogId, metric, size };
}

export const routeLayouts: Record<string, RouteLayout> = {
  overview: {
    title: 'Overview',
    description: 'High-level agent telemetry summary',
    panels: [
      panel('tokens_per_minute', 'kpi'),
      panel('active_sessions', 'kpi'),
      panel('cost_today', 'kpi'),
      panel('cache_hit_rate', 'kpi'),
      panel('safety_violations', 'kpi'),
      panel('budget_status', 'kpi'),
    ],
  },
  tokens: {
    title: 'Token Analytics',
    description: 'Input, output, and cache token consumption',
    panels: [
      panel('tokens_input_total', 'kpi'),
      panel('tokens_output_total', 'kpi'),
      panel('tokens_cache_read', 'kpi'),
      panel('tokens_cache_write', 'kpi'),
      panel('tokens_input_rate', 'half'),
      panel('tokens_output_rate', 'half'),
      panel('tokens_by_model', 'half'),
      panel('tokens_cache_hit_rate_ts', 'half'),
    ],
  },
  cost: {
    title: 'Cost Analytics',
    description: 'LLM spending, cache savings, and budget tracking',
    panels: [
      panel('cost_total', 'kpi'),
      panel('cost_cache_savings', 'kpi'),
      panel('cost_budget_remaining', 'kpi'),
      panel('cost_rate', 'half'),
      panel('cost_by_model', 'half'),
    ],
  },
  sessions: {
    title: 'Session Analytics',
    description: 'Conversation sessions, turns, and duration',
    panels: [
      panel('sessions_total', 'kpi'),
      panel('sessions_active', 'kpi'),
      panel('sessions_turns_avg', 'kpi'),
      panel('sessions_duration_avg', 'kpi'),
      panel('sessions_active_ts', 'half'),
      panel('sessions_turns_ts', 'half'),
    ],
  },
  tools: {
    title: 'Tool Analytics',
    description: 'Tool execution counts, latency, errors, and usefulness',
    panels: [
      panel('tools_calls_total', 'kpi'),
      panel('tools_errors_total', 'kpi'),
      panel('tools_avg_latency', 'kpi'),
      panel('tools_usefulness_avg', 'kpi'),
      panel('tools_calls_by_tool', 'half'),
      panel('tools_latency_by_tool', 'half'),
      panel('tools_error_rate', 'full'),
    ],
  },
  safety: {
    title: 'Content Safety',
    description: 'Content safety violations, blocks, and categories',
    panels: [
      panel('safety_total', 'kpi'),
      panel('safety_blocked', 'kpi'),
      panel('safety_checks_total', 'kpi'),
      panel('safety_violations_ts', 'half'),
      panel('safety_by_category', 'half'),
      panel('safety_block_rate', 'full'),
    ],
  },
  rag: {
    title: 'RAG Analytics',
    description: 'Document ingestion, retrieval latency, and top sources',
    panels: [
      panel('rag_ingestion_total', 'kpi'),
      panel('rag_retrieval_total', 'kpi'),
      panel('rag_avg_latency', 'kpi'),
      panel('rag_chunks_avg', 'kpi'),
      panel('rag_ingestion_rate', 'half'),
      panel('rag_retrieval_latency_ts', 'half'),
      panel('rag_by_source', 'full'),
    ],
  },
  budget: {
    title: 'Budget Management',
    description: 'Spending limits, utilization, and alerts',
    panels: [
      panel('budget_spent', 'kpi'),
      panel('budget_limit', 'kpi'),
      panel('budget_remaining', 'kpi'),
      panel('budget_utilization', 'half'),
      panel('budget_spend_rate', 'half'),
      panel('budget_alerts_total', 'kpi'),
    ],
  },
};
