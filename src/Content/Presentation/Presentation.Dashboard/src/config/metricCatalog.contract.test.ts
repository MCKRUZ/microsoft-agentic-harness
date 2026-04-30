import { describe, it, expect } from 'vitest';
import { metricCatalog, getCatalogByCategory } from './metricCatalog';

const OVERVIEW_IDS = ['tokens_per_minute', 'active_sessions', 'cost_today', 'cache_hit_rate', 'safety_violations', 'budget_status'];
const TOKENS_IDS = ['tokens_input_total', 'tokens_output_total', 'tokens_cache_read', 'tokens_cache_write', 'tokens_input_rate', 'tokens_output_rate', 'tokens_by_model', 'tokens_cache_hit_rate_ts'];
const COST_IDS = ['cost_total', 'cost_rate', 'cost_by_model', 'cost_cache_savings', 'cost_budget_remaining'];
const SESSIONS_IDS = ['sessions_total', 'sessions_active', 'sessions_turns_avg', 'sessions_duration_avg', 'sessions_active_ts', 'sessions_turns_ts'];
const TOOLS_IDS = ['tools_calls_total', 'tools_errors_total', 'tools_avg_latency', 'tools_result_size', 'tools_calls_by_tool', 'tools_latency_by_tool', 'tools_error_rate'];
const SAFETY_IDS = ['safety_total', 'safety_blocked', 'safety_checks_total', 'safety_violations_ts', 'safety_by_category', 'safety_block_rate'];
const RAG_IDS = ['rag_ingestion_total', 'rag_retrieval_total', 'rag_avg_latency', 'rag_chunks_avg', 'rag_ingestion_rate', 'rag_retrieval_latency_ts', 'rag_by_source'];
const BUDGET_IDS = ['budget_spent', 'budget_limit', 'budget_remaining', 'budget_utilization', 'budget_spend_rate', 'budget_status'];

const ALL_PAGE_IDS = [
  ...OVERVIEW_IDS, ...TOKENS_IDS, ...COST_IDS, ...SESSIONS_IDS,
  ...TOOLS_IDS, ...SAFETY_IDS, ...RAG_IDS, ...BUDGET_IDS,
];

describe('metricCatalog contract', () => {
  it.each(ALL_PAGE_IDS)('metric "%s" exists in catalog with a valid PromQL query', (id) => {
    const entry = metricCatalog[id];
    expect(entry).toBeDefined();
    expect(entry!.query).toBeTruthy();
    expect(entry!.query).toContain('agentic_harness');
  });

  it('all catalog IDs are consumed by at least one page', () => {
    const catalogIds = Object.keys(metricCatalog);
    const unusedIds = catalogIds.filter((id) => !ALL_PAGE_IDS.includes(id));
    expect(unusedIds).toEqual([]);
  });

  it.each(['overview', 'tokens', 'cost', 'sessions', 'tools', 'safety', 'rag', 'budget'])(
    'category "%s" has entries',
    (category) => {
      const entries = getCatalogByCategory(category);
      expect(entries.length).toBeGreaterThan(0);
    },
  );

  it('no duplicate metric IDs', () => {
    const ids = Object.keys(metricCatalog);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('every entry has all required fields', () => {
    for (const entry of Object.values(metricCatalog)) {
      expect(entry.id).toBeTruthy();
      expect(entry.title).toBeTruthy();
      expect(entry.query).toBeTruthy();
      expect(entry.chartType).toBeTruthy();
      expect(entry.unit).toBeTruthy();
      expect(entry.category).toBeTruthy();
    }
  });
});
