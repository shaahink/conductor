import type { StageDto } from "../api/types.js";

export type PlanRow =
  | { kind: "stage"; stage: StageDto; expanded: boolean }
  | { kind: "checkpoint"; stage: StageDto; checkpointIdx: number };

/** Flattens stages (+ their checkpoints, when expanded) into the row list the tree renders — the
 * exact same function drives click-to-row mapping in App.tsx, so "what you see is what you can
 * click" never drifts between render and input handling. The current stage is expanded by
 * default; anything in `overrides` wins over that default. */
export function computePlanRows(stages: StageDto[], currentStageId: string, overrides: Record<string, boolean>): PlanRow[] {
  const rows: PlanRow[] = [];
  for (const stage of stages) {
    const defaultExpanded = stage.id === currentStageId;
    const expanded = overrides[stage.id] ?? defaultExpanded;
    rows.push({ kind: "stage", stage, expanded });
    if (expanded) {
      stage.checkpoints.forEach((_, i) => rows.push({ kind: "checkpoint", stage, checkpointIdx: i }));
    }
  }
  return rows;
}
