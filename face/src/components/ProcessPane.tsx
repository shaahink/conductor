import React from "react";
import { Box, Text } from "ink";
import type { ProcessDto } from "../api/types.js";
import { colors } from "../theme.js";

export interface ProcessPaneProps {
  processes: ProcessDto[];
  width: number;
  height: number;
  selected: number;
  focused: boolean;
}

function fmtRuntime(p: ProcessDto): string {
  const start = new Date(p.startedUtc).getTime();
  const end = p.exitedUtc ? new Date(p.exitedUtc).getTime() : Date.now();
  const sec = Math.max(0, Math.floor((end - start) / 1000));
  const m = Math.floor(sec / 60);
  const s = sec % 60;
  return m > 0 ? `${m}m${s.toString().padStart(2, "0")}s` : `${s}s`;
}

/** F6.4 Process pane (D11): "what is it actually doing right now" at a glance — PID, purpose,
 * runtime, last output line for every supervised child (ProcessSupervisor + PID registry, F2). */
export function ProcessPane({ processes, width, height, selected, focused }: ProcessPaneProps) {
  if (processes.length === 0) {
    return (
      <Box width={width} height={height} paddingX={1}>
        <Text color={colors.dim}>no supervised processes right now</Text>
      </Box>
    );
  }

  const showLastLine = width >= 70;
  const visibleRows = Math.max(1, height);
  let top = 0;
  if (selected >= visibleRows) top = selected - visibleRows + 1;
  const shown = processes.slice(top, top + visibleRows);

  return (
    <Box flexDirection="column" width={width} height={height}>
      {shown.map((p, i) => {
        const idx = top + i;
        const isSelected = idx === selected;
        const bg = isSelected ? (focused ? colors.accentDim : "gray") : undefined;
        const aliveColor = p.alive ? colors.ok : colors.dim;
        return (
          // Same flexShrink={0} discipline as PlanTree.tsx (see its comment): pins every fixed
          // segment so only a flexGrow title/last-output Box ever gives up space to fit the row.
          <Box key={p.pid} width={width}>
            <Box flexShrink={0}>
              <Text backgroundColor={bg} color={aliveColor}>
                {p.alive ? "●" : "○"}{" "}
              </Text>
            </Box>
            <Box flexShrink={0}>
              <Text backgroundColor={bg}>{p.pid} </Text>
            </Box>
            <Box flexGrow={showLastLine ? 0 : 1} flexShrink={showLastLine ? 0 : 1}>
              <Text backgroundColor={bg} wrap="truncate-end">
                {p.purpose}
              </Text>
            </Box>
            <Box flexShrink={0}>
              <Text backgroundColor={bg} color={colors.dim}>
                {" "}
                {fmtRuntime(p)}
              </Text>
            </Box>
            {showLastLine && (
              <Box flexGrow={1} flexShrink={1}>
                <Text backgroundColor={bg} color={colors.dim} wrap="truncate-end">
                  {"  "}
                  {p.lastOutputLine ?? "—"}
                </Text>
              </Box>
            )}
          </Box>
        );
      })}
    </Box>
  );
}
