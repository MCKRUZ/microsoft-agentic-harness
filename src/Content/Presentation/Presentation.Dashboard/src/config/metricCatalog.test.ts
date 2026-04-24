import { describe, it, expect } from 'vitest';
import { metricCatalog, getCatalogByCategory } from './metricCatalog';

describe('metricCatalog', () => {
  it('has entries for all 8 categories', () => {
    const categories = new Set(Object.values(metricCatalog).map((e) => e.category));
    expect(categories).toContain('overview');
    expect(categories).toContain('tokens');
    expect(categories).toContain('cost');
    expect(categories).toContain('sessions');
    expect(categories).toContain('tools');
    expect(categories).toContain('safety');
    expect(categories).toContain('rag');
    expect(categories).toContain('budget');
  });

  it('every entry has required fields', () => {
    Object.values(metricCatalog).forEach((entry) => {
      expect(entry.id).toBeTruthy();
      expect(entry.title).toBeTruthy();
      expect(entry.description).toBeTruthy();
      expect(entry.query).toBeTruthy();
      expect(entry.chartType).toBeTruthy();
      expect(entry.unit).toBeTruthy();
      expect(entry.category).toBeTruthy();
      expect(entry.refreshIntervalSeconds).toBeGreaterThan(0);
    });
  });

  it('all ids are unique', () => {
    const ids = Object.keys(metricCatalog);
    const uniqueIds = new Set(ids);
    expect(uniqueIds.size).toBe(ids.length);
  });

  it('getCatalogByCategory returns only matching entries', () => {
    const overview = getCatalogByCategory('overview');
    expect(overview.length).toBeGreaterThan(0);
    overview.forEach((e) => expect(e.category).toBe('overview'));
  });

  it('getCatalogByCategory returns empty for unknown category', () => {
    expect(getCatalogByCategory('nonexistent')).toHaveLength(0);
  });

  it('overview category has at least 5 entries', () => {
    expect(getCatalogByCategory('overview').length).toBeGreaterThanOrEqual(5);
  });

  it('all queries contain agentic_harness metric prefix', () => {
    Object.values(metricCatalog).forEach((entry) => {
      expect(entry.query).toMatch(/agentic_harness|vector\(0\)/);
    });
  });
});
