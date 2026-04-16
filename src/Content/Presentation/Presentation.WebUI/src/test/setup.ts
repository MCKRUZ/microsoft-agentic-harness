import '@testing-library/jest-dom';
import { vi } from 'vitest';

// Mock matchMedia — not implemented in jsdom
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: vi.fn().mockImplementation((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  })),
});

// scrollIntoView — not implemented in jsdom
Element.prototype.scrollIntoView = vi.fn();

// MSW server setup added in section 12
