import React from "react";
import { Box, Text } from "ink";
import type { StateDto } from "../api/types.js";
import { colors, glyphFor, stateColor } from "../theme.js";
import { computePlanRows, type PlanRow } from "./planRows.js";

export interface PlanTreeProps {
  state: StateDto | null;
  selected: number;
  expandedOverrides: Record<string, boolean>;
  focused: boolean;
  width: number;
  height: number;
}

/** F6.2: tree with per-stage state/score/cost, current stage highlighted, checkpoints expandable.
 * Column widths scale with `width` instead of truncating stage/checkpoint titles at 100+ cols
 * (design doc D11: "the 4-char column bug class is a release blocker"). */
export function PlanTree({ state, selected, expandedOverrides, focused, width, height }: PlanTreeProps) {
  if (!state) {
    return (
      <Box flexDirection="column" width={width} height={height} paddingX={1}>
        <Text color={colors.dim}>waiting for plan state…</Text>
      </Box>
    );
  }

  const rows = computePlanRows(state.stages, state.stageId, expandedOverrides);
  const wide = width >= 70;
  const showCostCol = width >= 60;
  const showAttemptsCol = width >= 90;

  const visibleRows = Math.max(1, height);
  let top = 0;
  if (selected >= visibleRows) top = selected - visibleRows + 1;
  const shown = rows.slice(top, top + visibleRows);

  return (
    <Box flexDirection="column" width={width} height={height}>
      {shown.map((row, i) => {
        const idx = top + i;
        return (
          <PlanRowView
            key={idx}
            row={row}
            isSelected={idx === selected}
            focused={focused}
            wide={wide}
            showCostCol={showCostCol}
            showAttemptsCol={showAttemptsCol}
            width={width}
            currentCheckpointId={state.currentCheckpoint}
          />
        );
      })}
      {rows.length === 0 && <Text color={colors.dim}>no stages yet</Text>}
    </Box>
  );
}

function PlanRowView({
  row,
  isSelected,
  focused,
  wide,
  showCostCol,
  showAttemptsCol,
  width,
  currentCheckpointId,
}: {
  row: PlanRow;
  isSelected: boolean;
  focused: boolean;
  wide: boolean;
  showCostCol: boolean;
  showAttemptsCol: boolean;
  width: number;
  currentCheckpointId: string;
}) {
  const bg = isSelected ? (focused ? colors.accentDim : "gray") : undefined;
  const indent = row.kind === "stage" ? "" : "  ";
  const caret = row.kind === "stage" ? (row.expanded ? "▾" : "▸") : " ";

  // NOTE: every fixed-size piece below is wrapped in <Box flexShrink={0}> and is its OWN sibling
  // <Text> (never nested Text-inside-Text). Two distinct Yoga/Ink measurement pitfalls, both caught
  // by the golden snapshot test at 120/200 cols: (1) nesting a styled <Text> inside another <Text>
  // under-measures the outer node's width, corrupting the flexGrow title's column math ("F5"
  // rendered as "F", title glued on with no space); (2) without flexShrink={0}, Yoga's default
  // "everything can shrink" happily shrinks a *fixed* Text (eating a trailing space) instead of only
  // shrinking the flexGrow title when a row's natural width slightly overflows — most visible on the
  // one row with an extra trailing "← current" suffix. flexShrink={0} pins every fixed segment so
  // only the title's flexGrow Box ever gives up space.
  if (row.kind === "stage") {
    const s = row.stage;
    const glyph = glyphFor(s.state);
    const color = stateColor(s.state);
    const progress = `${s.done}/${s.total}`;
    return (
      <Box width={width}>
        <Box flexShrink={0}>
          <Text backgroundColor={bg}>{caret} </Text>
        </Box>
        <Box flexShrink={0}>
          <Text backgroundColor={bg} color={color}>
            {glyph}{" "}
          </Text>
        </Box>
        <Box flexShrink={0}>
          <Text backgroundColor={bg} bold={s.state === "active"}>
            {s.id}{" "}
          </Text>
        </Box>
        <Box flexGrow={1} flexShrink={1}>
          <Text backgroundColor={bg} wrap="truncate-end">
            {s.title}
          </Text>
        </Box>
        <Box flexShrink={0}>
          <Text backgroundColor={bg} color={colors.dim}>
            {" "}
            {progress}
          </Text>
        </Box>
        {showCostCol && (
          <Box flexShrink={0}>
            <Text backgroundColor={bg} color={colors.dim}>
              {"  $"}
              {s.costUsd.toFixed(2)}
            </Text>
          </Box>
        )}
        {showAttemptsCol && wide && (
          <Box flexShrink={0}>
            <Text backgroundColor={bg} color={colors.dim}>
              {"  "}attempts={s.attempts} {s.lastOutcome}
            </Text>
          </Box>
        )}
      </Box>
    );
  }

  const cp = row.stage.checkpoints[row.checkpointIdx];
  if (!cp) return null;
  const glyph = glyphFor(cp.status);
  const color = stateColor(cp.status);
  const isCurrent = cp.id === currentCheckpointId;
  return (
    <Box width={width}>
      <Box flexShrink={0}>
        <Text backgroundColor={bg}>
          {indent}
          {caret}{" "}
        </Text>
      </Box>
      <Box flexShrink={0}>
        <Text backgroundColor={bg} color={color}>
          {glyph}{" "}
        </Text>
      </Box>
      <Box flexShrink={0}>
        <Text backgroundColor={bg}>{cp.id} </Text>
      </Box>
      <Box flexGrow={1} flexShrink={1}>
        <Text backgroundColor={bg} wrap="truncate-end" dimColor={cp.status === "DONE"}>
          {cp.title}
        </Text>
      </Box>
      {isCurrent && (
        <Box flexShrink={0}>
          <Text backgroundColor={bg} color={colors.accent}>
            {" "}
            ← current
          </Text>
        </Box>
      )}
    </Box>
  );
}
