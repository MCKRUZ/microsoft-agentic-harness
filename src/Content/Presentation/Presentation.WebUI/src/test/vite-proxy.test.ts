import { describe, it, expect } from 'vitest'
import config from '../../vite.config'

// Verifies that the Vite dev server proxy is configured correctly so that
// /api/* and /hubs/* requests are forwarded to the AgentHub backend without CORS issues.
describe('vite dev server proxy config', () => {
  const proxy = (config as Record<string, unknown> & { server?: { proxy?: Record<string, unknown> } }).server?.proxy

  it('forwards /api requests to localhost:50772 with changeOrigin', () => {
    const api = proxy?.['/api'] as { target: string; changeOrigin: boolean } | undefined
    expect(api?.target).toBe('http://localhost:50772')
    expect(api?.changeOrigin).toBe(true)
  })

  it('forwards /hubs requests to localhost:50772 with WebSocket support and changeOrigin', () => {
    const hubs = proxy?.['/hubs'] as { target: string; ws: boolean; changeOrigin: boolean } | undefined
    expect(hubs?.target).toBe('http://localhost:50772')
    expect(hubs?.ws).toBe(true)
    expect(hubs?.changeOrigin).toBe(true)
  })
})
