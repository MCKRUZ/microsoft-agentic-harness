import { describe, it, expect } from 'vitest';
import { queryInstant, queryRange, getCatalog, getHealth } from './metrics';

describe('metrics API', () => {
  it('queryRange returns successful response', async () => {
    const now = Math.floor(Date.now() / 1000);
    const result = await queryRange('test_query', String(now - 3600), String(now), '15s');
    expect(result.success).toBe(true);
    expect(result.series).toHaveLength(1);
    expect(result.series[0]!.dataPoints.length).toBeGreaterThan(0);
  });

  it('queryInstant returns successful response', async () => {
    const result = await queryInstant('test_query');
    expect(result.success).toBe(true);
    expect(result.series).toHaveLength(1);
  });

  it('getCatalog returns catalog entries', async () => {
    const catalog = await getCatalog();
    expect(catalog.length).toBeGreaterThan(0);
    expect(catalog[0]).toHaveProperty('id');
    expect(catalog[0]).toHaveProperty('title');
    expect(catalog[0]).toHaveProperty('query');
  });

  it('getHealth returns healthy status', async () => {
    const health = await getHealth();
    expect(health.healthy).toBe(true);
    expect(health.version).toBe('2.51.0');
  });
});
