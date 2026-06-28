import { router } from '@/app/router';
import { queryClient } from '@/app/queryClient';
import { asString } from '@/lib/utils';
import { useTimeRangeStore, type TimeRangePreset } from '@/stores/timeRangeStore';
import { useThemeStore } from '@/stores/themeStore';

/** Parameters supplied by the agent for a `dashboard_control` operation. */
export type ActionParams = Record<string, unknown>;

/** Presets the agent may select via `set_time_range` (excludes the custom sentinel). */
const SELECTABLE_PRESETS: readonly TimeRangePreset[] = ['1h', '6h', '24h', '7d'];

/** Reads the current view state so the agent can reason about what the user is looking at. */
function getState(): string {
  const tr = useTimeRangeStore.getState();
  const theme = useThemeStore.getState().theme;
  return JSON.stringify({
    page: router.state.location.pathname,
    preset: tr.preset,
    customStart: tr.customStart,
    customEnd: tr.customEnd,
    refreshIntervalSeconds: tr.refreshIntervalSeconds,
    theme,
  });
}

/** Changes the dashboard time range via a preset or an explicit custom from/to window. */
function setTimeRange(params: ActionParams): string {
  const tr = useTimeRangeStore.getState();

  const preset = asString(params['preset']);
  if (preset) {
    if (!SELECTABLE_PRESETS.includes(preset as TimeRangePreset)) {
      return `Unsupported preset "${preset}". Valid presets: ${SELECTABLE_PRESETS.join(', ')}.`;
    }
    tr.setPreset(preset as TimeRangePreset);
    return `Time range set to ${preset}.`;
  }

  const from = asString(params['from']);
  const to = asString(params['to']);
  if (from && to) {
    tr.setCustomRange(from, to);
    return `Time range set to custom window ${from} → ${to}.`;
  }

  return 'No valid time range supplied. Provide a preset (1h, 6h, 24h, 7d) or both from and to.';
}

/** Navigates the dashboard to the given route path. */
function navigate(params: ActionParams): string {
  const path = asString(params['path']);
  if (!path || !path.startsWith('/')) {
    return 'No valid path supplied. Provide a path beginning with "/", e.g. /spend/tokens.';
  }
  void router.navigate(path);
  return `Navigated to ${path}.`;
}

/** Refreshes dashboard data by invalidating cached queries (matches the Topbar refresh button). */
function refreshData(): string {
  void queryClient.invalidateQueries();
  return 'Refreshed the dashboard data.';
}

/**
 * Maps a `dashboard_control` operation to a concrete dashboard effect and returns a short result
 * string for the agent to observe. Unknown operations return an explanatory message rather than
 * throwing, so a misbehaving agent never breaks the run.
 */
export async function dispatchDashboardAction(
  operation: string,
  params: ActionParams,
): Promise<string> {
  switch (operation) {
    case 'get_state':
      return getState();
    case 'set_time_range':
      return setTimeRange(params);
    case 'navigate':
      return navigate(params);
    case 'refresh_data':
      return refreshData();
    default:
      return `Unknown dashboard operation "${operation}".`;
  }
}

/** A short, human-readable label for the activity indicator while an action runs. */
export function describeAction(operation: string, params: ActionParams): string {
  switch (operation) {
    case 'get_state':
      return 'Reading the current view';
    case 'set_time_range':
      return `Setting time range${asString(params['preset']) ? ` → ${asString(params['preset'])}` : ''}`;
    case 'navigate':
      return `Navigating${asString(params['path']) ? ` → ${asString(params['path'])}` : ''}`;
    case 'refresh_data':
      return 'Refreshing data';
    default:
      return operation;
  }
}
