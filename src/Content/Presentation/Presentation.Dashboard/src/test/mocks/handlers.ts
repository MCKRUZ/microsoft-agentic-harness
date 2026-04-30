import { http, HttpResponse } from 'msw';
import type { MetricsQueryResponse, SessionRecord, SessionDetail, PrometheusHealthResponse } from '@/api/types';

const now = Math.floor(Date.now() / 1000);

function makeTimeSeries(baseValue: number, points = 20): MetricsQueryResponse {
  return {
    success: true,
    resultType: 'matrix',
    series: [
      {
        labels: { __name__: 'metric' },
        dataPoints: Array.from({ length: points }, (_, i) => ({
          timestamp: now - (points - i) * 60,
          value: String(baseValue + Math.sin(i / 3) * baseValue * 0.15),
        })),
      },
    ],
  };
}

function makeMultiSeries(
  labelKey: string,
  labelValues: string[],
  baseValues: number[],
  points = 20,
): MetricsQueryResponse {
  return {
    success: true,
    resultType: 'matrix',
    series: labelValues.map((label, idx) => ({
      labels: { [labelKey]: label },
      dataPoints: Array.from({ length: points }, (_, i) => ({
        timestamp: now - (points - i) * 60,
        value: String((baseValues[idx] ?? 10) + Math.sin(i / 3) * (baseValues[idx] ?? 10) * 0.1),
      })),
    })),
  };
}

function routeMetricsQuery(query: string): MetricsQueryResponse {
  if (query.includes('by (model)')) {
    return makeMultiSeries('model', ['claude-3-opus', 'claude-3-sonnet', 'gpt-4o'], [5000, 12000, 3000]);
  }
  if (query.includes('by (agent_tool_name)')) {
    return makeMultiSeries('agent_tool_name', ['file_search', 'code_exec', 'web_fetch'], [150, 80, 45]);
  }
  if (query.includes('by (category)')) {
    return makeMultiSeries('category', ['violence', 'sexual_content', 'hate_speech'], [5, 2, 1]);
  }
  if (query.includes('by (source)') || query.includes('topk')) {
    return makeMultiSeries('source', ['docs.md', 'readme.md', 'api-ref.md'], [120, 85, 60]);
  }

  if (query.includes('cache_hit_rate')) return makeTimeSeries(0.72);
  if (query.includes('budget_utilization')) return makeTimeSeries(0.45);
  if (query.includes('budget_status')) return makeTimeSeries(0);
  if (query.includes('budget_current_spend')) return makeTimeSeries(12.5);
  if (query.includes('budget_threshold')) return makeTimeSeries(50.0);
  if (query.includes('budget_remaining')) return makeTimeSeries(37.5);
  if (query.includes('cost')) return makeTimeSeries(0.0523);
  if (query.includes('safety_blocks')) return makeTimeSeries(3);
  if (query.includes('safety')) return makeTimeSeries(15);
  if (query.includes('session_active')) return makeTimeSeries(3);
  if (query.includes('sessions_started')) return makeTimeSeries(25);
  if (query.includes('turns_per_conversation')) return makeTimeSeries(8.5);
  if (query.includes('conversation_duration')) return makeTimeSeries(180000);
  if (query.includes('tokens_input')) return makeTimeSeries(125000);
  if (query.includes('tokens_output')) return makeTimeSeries(45000);
  if (query.includes('tokens_cache_read')) return makeTimeSeries(80000);
  if (query.includes('tokens_cache_write')) return makeTimeSeries(15000);
  if (query.includes('tokens_total') || query.includes('tokens_cost')) return makeTimeSeries(170000);
  if (query.includes('tool_duration')) return makeTimeSeries(250);
  if (query.includes('tool_invocations')) return makeTimeSeries(150);
  if (query.includes('tool_errors')) return makeTimeSeries(5);
  if (query.includes('tool_result_size')) return makeTimeSeries(2048);
  if (query.includes('rag_ingestion')) return makeTimeSeries(50);
  if (query.includes('rag_retrieval_duration')) return makeTimeSeries(120);
  if (query.includes('rag_retrieval_chunks')) return makeTimeSeries(4.2);
  if (query.includes('rag_retrieval')) return makeTimeSeries(200);
  if (query.includes('user_activity_turns')) return makeTimeSeries(45);
  if (query.includes('user_activity_cost')) return makeTimeSeries(0.035);
  if (query.includes('rate(')) return makeTimeSeries(450);

  return makeTimeSeries(42);
}

const mockSessions: SessionRecord[] = [
  {
    id: '11111111-1111-1111-1111-111111111111',
    conversationId: 'conv-1',
    agentName: 'CodeAssistant',
    model: 'claude-3-opus',
    startedAt: new Date(Date.now() - 3600000).toISOString(),
    endedAt: new Date().toISOString(),
    durationMs: 3600000,
    turnCount: 12,
    toolCallCount: 5,
    subagentCount: 2,
    totalInputTokens: 45000,
    totalOutputTokens: 12000,
    totalCacheRead: 30000,
    totalCacheWrite: 5000,
    totalCostUsd: 0.0234,
    cacheHitRate: 0.667,
    status: 'completed',
    errorMessage: null,
    createdAt: new Date(Date.now() - 3600000).toISOString(),
  },
  {
    id: '22222222-2222-2222-2222-222222222222',
    conversationId: 'conv-2',
    agentName: 'ResearchAgent',
    model: 'claude-3-sonnet',
    startedAt: new Date(Date.now() - 1800000).toISOString(),
    endedAt: null,
    durationMs: null,
    turnCount: 5,
    toolCallCount: 3,
    subagentCount: 0,
    totalInputTokens: 22000,
    totalOutputTokens: 8000,
    totalCacheRead: 12000,
    totalCacheWrite: 3000,
    totalCostUsd: 0.0089,
    cacheHitRate: 0.545,
    status: 'active',
    errorMessage: null,
    createdAt: new Date(Date.now() - 1800000).toISOString(),
  },
  {
    id: '33333333-3333-3333-3333-333333333333',
    conversationId: 'conv-3',
    agentName: 'CodeAssistant',
    model: 'gpt-4o',
    startedAt: new Date(Date.now() - 7200000).toISOString(),
    endedAt: new Date(Date.now() - 5400000).toISOString(),
    durationMs: 1800000,
    turnCount: 8,
    toolCallCount: 2,
    subagentCount: 1,
    totalInputTokens: 35000,
    totalOutputTokens: 10000,
    totalCacheRead: 0,
    totalCacheWrite: 0,
    totalCostUsd: 0.0456,
    cacheHitRate: 0,
    status: 'completed',
    errorMessage: null,
    createdAt: new Date(Date.now() - 7200000).toISOString(),
  },
];

const mockSessionDetail: SessionDetail = {
  session: mockSessions[0]!,
  messages: [
    {
      id: 'msg-1',
      sessionId: '11111111-1111-1111-1111-111111111111',
      turnIndex: 0,
      role: 'user',
      source: null,
      contentPreview: 'Help me refactor the authentication module',
      model: null,
      inputTokens: 150,
      outputTokens: 0,
      cacheRead: 0,
      cacheWrite: 0,
      costUsd: 0,
      cacheHitPct: 0,
      toolNames: null,
      createdAt: new Date(Date.now() - 3600000).toISOString(),
    },
    {
      id: 'msg-2',
      sessionId: '11111111-1111-1111-1111-111111111111',
      turnIndex: 1,
      role: 'assistant',
      source: 'CodeAssistant',
      contentPreview: 'I\'ll analyze the current authentication module and suggest improvements...',
      model: 'claude-3-opus',
      inputTokens: 5000,
      outputTokens: 2000,
      cacheRead: 3000,
      cacheWrite: 500,
      costUsd: 0.0045,
      cacheHitPct: 0.6,
      toolNames: ['file_search', 'code_exec'],
      createdAt: new Date(Date.now() - 3500000).toISOString(),
    },
  ],
  tools: [
    {
      id: 'tool-1',
      sessionId: '11111111-1111-1111-1111-111111111111',
      messageId: 'msg-2',
      toolName: 'file_search',
      toolSource: 'builtin',
      durationMs: 120,
      status: 'success',
      errorType: null,
      resultSize: 4096,
      createdAt: new Date(Date.now() - 3500000).toISOString(),
    },
    {
      id: 'tool-2',
      sessionId: '11111111-1111-1111-1111-111111111111',
      messageId: 'msg-2',
      toolName: 'code_exec',
      toolSource: 'builtin',
      durationMs: 450,
      status: 'success',
      errorType: null,
      resultSize: 2048,
      createdAt: new Date(Date.now() - 3490000).toISOString(),
    },
  ],
  safetyEvents: [
    {
      id: 'safety-1',
      sessionId: '11111111-1111-1111-1111-111111111111',
      phase: 'input',
      outcome: 'pass',
      category: 'violence',
      severity: 0,
      filterName: 'ContentSafetyFilter',
      createdAt: new Date(Date.now() - 3500000).toISOString(),
    },
  ],
};

export const handlers = [
  http.get('/api/metrics/range', ({ request }) => {
    const url = new URL(request.url);
    const query = url.searchParams.get('query') ?? '';
    return HttpResponse.json(routeMetricsQuery(query));
  }),

  http.get('/api/metrics/instant', ({ request }) => {
    const url = new URL(request.url);
    const query = url.searchParams.get('query') ?? '';
    const full = routeMetricsQuery(query);
    return HttpResponse.json({
      ...full,
      series: full.series.map((s) => ({
        ...s,
        dataPoints: s.dataPoints.slice(-1),
      })),
    });
  }),

  http.get('/api/metrics/catalog', () => {
    return HttpResponse.json([
      { id: 'test_metric', title: 'Test Metric', description: 'A test metric', query: 'test_query', chartType: 'stat', unit: 'count', category: 'overview', refreshIntervalSeconds: 15 },
    ]);
  }),

  http.get('/api/metrics/health', () => {
    return HttpResponse.json({
      healthy: true,
      version: '2.51.0',
    } satisfies PrometheusHealthResponse);
  }),

  http.get('/api/sessions', () => {
    return HttpResponse.json(mockSessions);
  }),

  http.get('/api/sessions/:id', ({ params }) => {
    const id = params['id'] as string;
    if (id === mockSessions[0]!.id) {
      return HttpResponse.json(mockSessionDetail);
    }
    return HttpResponse.json(
      { ...mockSessionDetail, session: { ...mockSessionDetail.session, id } },
    );
  }),
];
