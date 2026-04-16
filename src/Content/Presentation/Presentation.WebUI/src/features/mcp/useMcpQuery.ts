import { z } from 'zod';
import { useQuery, useMutation } from '@tanstack/react-query';
import { apiClient } from '@/lib/apiClient';

const McpToolSchema = z.object({
  name: z.string(),
  description: z.string(),
  inputSchema: z.record(z.string(), z.unknown()),
});

const McpResourceSchema = z.object({
  uri: z.string(),
  name: z.string(),
  description: z.string().optional(),
});

const McpPromptArgumentSchema = z.object({
  name: z.string(),
  description: z.string().optional(),
  required: z.boolean().optional(),
});

const McpPromptSchema = z.object({
  name: z.string(),
  description: z.string().optional(),
  arguments: z.array(McpPromptArgumentSchema).optional(),
});

export type McpTool = z.infer<typeof McpToolSchema>;
export type McpResource = z.infer<typeof McpResourceSchema>;
export type McpPrompt = z.infer<typeof McpPromptSchema>;

export function useToolsQuery() {
  return useQuery<McpTool[]>({
    queryKey: ['mcp', 'tools'],
    queryFn: () =>
      apiClient.get('/api/mcp/tools').then((r) => z.array(McpToolSchema).parse(r.data)),
    staleTime: 60_000,
  });
}

export function useResourcesQuery() {
  return useQuery<McpResource[]>({
    queryKey: ['mcp', 'resources'],
    queryFn: () =>
      apiClient.get('/api/mcp/resources').then((r) => z.array(McpResourceSchema).parse(r.data)),
    staleTime: 60_000,
  });
}

export function usePromptsQuery() {
  return useQuery<McpPrompt[]>({
    queryKey: ['mcp', 'prompts'],
    queryFn: () =>
      apiClient.get('/api/mcp/prompts').then((r) => z.array(McpPromptSchema).parse(r.data)),
    staleTime: 60_000,
  });
}

export function useInvokeTool() {
  return useMutation<unknown, Error, { name: string; args: Record<string, unknown> }>({
    mutationFn: ({ name, args }) =>
      apiClient
        .post(`/api/mcp/tools/${encodeURIComponent(name)}/invoke`, { args })
        .then((r) => r.data),
  });
}
