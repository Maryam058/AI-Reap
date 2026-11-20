// AgentRunStatus / AgentStageStatus (agent step) as the API returns them, with display labels.
export const RUN_STATUS: Record<number, { label: string; tone: string }> = {
  0: { label: 'Running', tone: 'info' },
  1: { label: 'Awaiting decision', tone: 'warning' },
  2: { label: 'Completed', tone: 'success' },
  3: { label: 'Rejected', tone: 'danger' },
  4: { label: 'Failed', tone: 'danger' },
};

export const STEP_STATUS: Record<number, { label: string; tone: string }> = {
  0: { label: 'Pending', tone: 'neutral' },
  1: { label: 'Running', tone: 'info' },
  2: { label: 'Awaiting decision', tone: 'warning' },
  3: { label: 'Approved', tone: 'approved' },
  4: { label: 'Rejected', tone: 'danger' },
  5: { label: 'Failed', tone: 'danger' },
};
