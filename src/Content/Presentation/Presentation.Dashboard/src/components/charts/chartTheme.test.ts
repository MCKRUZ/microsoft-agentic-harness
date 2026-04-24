import { describe, it, expect } from 'vitest';
import { CHART_COLORS, getChartColor } from './chartTheme';

describe('chartTheme', () => {
  it('has 8 chart colors', () => {
    expect(CHART_COLORS).toHaveLength(8);
  });

  it('all colors reference CSS variables', () => {
    CHART_COLORS.forEach((color) => {
      expect(color).toMatch(/^var\(--chart-\d+\)$/);
    });
  });

  it('getChartColor returns correct color for index', () => {
    expect(getChartColor(0)).toBe('var(--chart-1)');
    expect(getChartColor(1)).toBe('var(--chart-2)');
    expect(getChartColor(7)).toBe('var(--chart-8)');
  });

  it('getChartColor wraps around for index >= length', () => {
    expect(getChartColor(8)).toBe('var(--chart-1)');
    expect(getChartColor(9)).toBe('var(--chart-2)');
    expect(getChartColor(16)).toBe('var(--chart-1)');
  });
});
