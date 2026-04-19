import { create } from 'zustand';

type AsyncAction = () => Promise<void>;

interface HubActionsState {
  joinGlobalTraces: AsyncAction | null;
  leaveGlobalTraces: AsyncAction | null;
  setActions: (actions: {
    joinGlobalTraces: AsyncAction;
    leaveGlobalTraces: AsyncAction;
  } | null) => void;
}

export const useHubActionsStore = create<HubActionsState>()((set) => ({
  joinGlobalTraces: null,
  leaveGlobalTraces: null,
  setActions: (actions) => set({
    joinGlobalTraces: actions?.joinGlobalTraces ?? null,
    leaveGlobalTraces: actions?.leaveGlobalTraces ?? null,
  }),
}));
