import { describe, expect, it } from "vitest";
import { computePlanRows } from "../src/components/planRows.js";
import { FIXTURE_STATE } from "./fixtures.js";

describe("computePlanRows", () => {
  it("auto-expands only the current stage by default", () => {
    const rows = computePlanRows(FIXTURE_STATE.stages, FIXTURE_STATE.stageId, {});
    // F5 (confirmed, not current) collapsed: 1 row. F6 (current) expanded: 1 + 5 checkpoints. F7 collapsed: 1 row.
    expect(rows).toHaveLength(1 + (1 + 5) + 1);
    expect(rows[0]).toMatchObject({ kind: "stage", stage: { id: "F5" }, expanded: false });
    expect(rows[1]).toMatchObject({ kind: "stage", stage: { id: "F6" }, expanded: true });
    expect(rows[2]).toMatchObject({ kind: "checkpoint", checkpointIdx: 0 });
  });

  it("an override forces a non-current stage open and the current stage closed", () => {
    const rows = computePlanRows(FIXTURE_STATE.stages, FIXTURE_STATE.stageId, { F5: true, F6: false });
    const f5Row = rows.find((r) => r.kind === "stage" && r.stage.id === "F5");
    const f6Row = rows.find((r) => r.kind === "stage" && r.stage.id === "F6");
    expect(f5Row).toMatchObject({ expanded: true });
    expect(f6Row).toMatchObject({ expanded: false });
    // F5 now contributes its 3 checkpoints instead of F6's 5.
    expect(rows).toHaveLength(1 + 3 + 1 + 1);
  });

  it("row order exactly matches click-to-row expectations (row N is visually the Nth line)", () => {
    const rows = computePlanRows(FIXTURE_STATE.stages, FIXTURE_STATE.stageId, {});
    // stage F5, stage F6, cp F6.1..F6.5, stage F7 — in that exact order.
    const labels = rows.map((r) => (r.kind === "stage" ? r.stage.id : r.stage.checkpoints[r.checkpointIdx]?.id));
    expect(labels).toEqual(["F5", "F6", "F6.1", "F6.2", "F6.3", "F6.4", "F6.5", "F7"]);
  });
});
