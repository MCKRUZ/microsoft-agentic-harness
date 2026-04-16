/**
 * Shared MSW server for the WebUI test suite.
 *
 * `setup.ts` starts this server with `onUnhandledRequest: 'error'`, meaning any
 * HTTP request made during tests that is NOT covered by a handler here (or by a
 * per-test `server.use(...)` override) will fail the test loudly.
 *
 * When adding new API routes to the app, add a matching handler below so
 * existing tests continue to pass under the shared server.
 */
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';

export const handlers = [
  http.get('http://localhost/api/agents', () =>
    HttpResponse.json([{ name: 'research-agent', description: 'A research agent' }]),
  ),

  http.get('http://localhost/api/mcp/tools', () =>
    HttpResponse.json([
      { name: 'get-time', description: 'Gets current time', inputSchema: { type: 'object', properties: {} } },
      { name: 'calculate', description: 'Performs calculation', inputSchema: { type: 'object', properties: {} } },
    ]),
  ),

  http.post('http://localhost/api/mcp/tools/:name/invoke', () =>
    HttpResponse.json({ result: 'tool executed successfully' }),
  ),

  http.get('http://localhost/api/mcp/resources', () =>
    HttpResponse.json([
      { uri: 'file://docs/readme.md', name: 'Readme', description: 'Project readme' },
    ]),
  ),

  http.get('http://localhost/api/mcp/prompts', () =>
    HttpResponse.json([
      { name: 'summarize', description: 'Summarize text', arguments: [{ name: 'text' }] },
    ]),
  ),
];

export const server = setupServer(...handlers);
