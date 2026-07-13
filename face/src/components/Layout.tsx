import React, { useEffect } from "react";
import { Box, Text } from "ink";
import { useAppState, useStoreInstance } from "../state/context.js";
import { useTerminalSize } from "../state/useTerminalSize.js";
import { PlanTree } from "./PlanTree.js";
import { AgentPane } from "./AgentPane.js";
import { ProcessPane } from "./ProcessPane.js";
import { Ticker } from "./Ticker.js";
import { ToastStack } from "./Toast.js";
import { colors } from "../theme.js";
import type { PaneId, Rect } from "../state/store.js";

export interface LayoutRects {
  wide: boolean;
  plan: Rect;
  agent: Rect;
  process: Rect;
}

/** Computes the responsive grid — wide terminals (>=100 cols) get a two-column layout (plan tree
 * left; agent pane + process pane stacked right), narrow terminals get a single stacked column.
 * Pure function of (columns, rows) so golden snapshot tests can assert on it directly. */
export function computeLayout(columns: number, rows: number): LayoutRects {
  const tickerHeight = 1;
  const footerHeight = 1;
  const bodyHeight = Math.max(3, rows - tickerHeight - footerHeight);
  const wide = columns >= 100;

  if (wide) {
    const planWidth = Math.min(50, Math.max(28, Math.floor(columns * 0.3)));
    const rightWidth = columns - planWidth;
    const processHeight = Math.min(10, Math.max(5, Math.floor(bodyHeight * 0.28)));
    const agentHeight = Math.max(4, bodyHeight - processHeight);
    return {
      wide,
      plan: { x: 0, y: tickerHeight, width: planWidth, height: bodyHeight },
      agent: { x: planWidth, y: tickerHeight, width: rightWidth, height: agentHeight },
      process: { x: planWidth, y: tickerHeight + agentHeight, width: rightWidth, height: processHeight },
    };
  }

  const planHeight = Math.max(5, Math.floor(bodyHeight * 0.4));
  const processHeight = Math.min(6, Math.max(3, Math.floor(bodyHeight * 0.22)));
  const agentHeight = Math.max(3, bodyHeight - planHeight - processHeight);
  return {
    wide,
    plan: { x: 0, y: tickerHeight, width: columns, height: planHeight },
    agent: { x: 0, y: tickerHeight + planHeight, width: columns, height: agentHeight },
    process: { x: 0, y: tickerHeight + planHeight + agentHeight, width: columns, height: processHeight },
  };
}

/** Content budget inside a bordered PaneFrame: 2 cols/1 row for the border, +1 more row for the
 * title line (a real row in the flow, not an overlay — see PaneFrame's comment for why). Exported
 * so Layout() can size a pane's child component to exactly match what PaneFrame actually gives it. */
export function contentSize(rect: Rect): { width: number; height: number } {
  return { width: Math.max(1, rect.width - 2), height: Math.max(1, rect.height - 3) };
}

function PaneFrame({ title, focused, width, height, children }: { id: PaneId; title: string; focused: boolean; width: number; height: number; children: React.ReactNode }) {
  const content = contentSize({ x: 0, y: 0, width, height });
  return (
    <Box flexDirection="column" width={width} height={height} borderStyle="round" borderColor={focused ? colors.accent : colors.dim}>
      {/* A genuine first row, not a negative-margin overlay onto the border — Ink/Yoga still counts
       * an overlaid row toward this Box's height even though it's drawn on top of the border, which
       * silently added 1 extra row per pane and pushed the footer into the last pane's bottom
       * border (caught by the golden snapshot test). */}
      <Text bold={focused} color={focused ? colors.accent : colors.dim}>
        {" "}
        {title}
      </Text>
      <Box flexDirection="column" width={content.width} height={content.height}>
        {children}
      </Box>
    </Box>
  );
}

export function Layout() {
  const app = useAppState();
  const store = useStoreInstance();
  const { columns, rows } = useTerminalSize();
  const layout = computeLayout(columns, rows);

  useEffect(() => {
    store.setRegion("plan", layout.plan);
    store.setRegion("agent", layout.agent);
    store.setRegion("process", layout.process);
  }, [layout.plan.x, layout.plan.y, layout.plan.width, layout.plan.height,
      layout.agent.x, layout.agent.y, layout.agent.width, layout.agent.height,
      layout.process.x, layout.process.y, layout.process.width, layout.process.height, store]);

  const { ui } = app;

  return (
    <Box flexDirection="column" width={columns} height={rows}>
      <Ticker state={app.planState} connection={app.connection} columns={columns} />
      <Box flexDirection={layout.wide ? "row" : "column"}>
        <PaneFrame id="plan" title="PLAN" focused={ui.focusedPane === "plan"} width={layout.plan.width} height={layout.plan.height}>
          <PlanTree
            state={app.planState}
            selected={ui.planSelected}
            expandedOverrides={ui.expandedOverrides}
            focused={ui.focusedPane === "plan"}
            {...contentSize(layout.plan)}
          />
        </PaneFrame>
        <Box flexDirection="column">
          <PaneFrame id="agent" title={`AGENT${app.planState ? ` — s${app.planState.sessionNumber} ${app.planState.sessionKind}` : ""}`} focused={ui.focusedPane === "agent"} width={layout.agent.width} height={layout.agent.height}>
            <AgentPane
              transcript={app.transcript}
              {...contentSize(layout.agent)}
              focused={ui.focusedPane === "agent"}
              autoScroll={ui.agentAutoScroll}
              offset={ui.agentOffset}
              search={ui.agentSearch}
              foldTools={ui.agentFoldTools}
              connected={app.connection.transcriptConnected}
            />
          </PaneFrame>
          <PaneFrame id="process" title="PROCESSES" focused={ui.focusedPane === "process"} width={layout.process.width} height={layout.process.height}>
            <ProcessPane
              processes={app.processes}
              {...contentSize(layout.process)}
              selected={ui.processSelected}
              focused={ui.focusedPane === "process"}
            />
          </PaneFrame>
        </Box>
      </Box>
      <Box paddingX={1} flexDirection="row" gap={1}>
        <Text color={colors.dim}>[</Text><Text color={colors.accent}>Tab</Text><Text color={colors.dim}>]pane</Text>
        <Text color={colors.dim}>[</Text><Text color={colors.accent}>:</Text><Text color={colors.dim}>]cmd</Text>
        <Text color={colors.dim}>[</Text><Text color={colors.accent}>i</Text><Text color={colors.dim}>]inject</Text>
        <Text color={colors.dim}>[</Text><Text color={colors.accent}>e</Text><Text color={colors.dim}>]templates</Text>
        <Text color={colors.dim}>[</Text><Text color={colors.accent}>h</Text><Text color={colors.dim}>]history</Text>
        <Text color={colors.dim}>[</Text><Text color={colors.accent}>r</Text><Text color={colors.dim}>]query</Text>
        <Text color={colors.dim}>[</Text><Text color={colors.accent}>?</Text><Text color={colors.dim}>]help</Text>
        <Text color={colors.dim}>[</Text><Text color={colors.accent}>q</Text><Text color={colors.dim}>]quit</Text>
      </Box>
      <ToastStack toasts={app.toasts} />
    </Box>
  );
}
