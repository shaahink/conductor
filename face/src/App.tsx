import React, { useEffect, useRef } from "react";
import { useApp, useInput, useStdin } from "ink";
import { useAppState, useStoreInstance } from "./state/context.js";
import { startConnection } from "./state/connection.js";
import type { DataSource } from "./api/dataSource.js";
import { Layout } from "./components/Layout.js";
import { CommandPalette } from "./components/CommandPalette.js";
import { InjectEditor } from "./components/InjectEditor.js";
import { PromptEditor } from "./components/PromptEditor.js";
import { SessionHistory } from "./components/SessionHistory.js";
import { ReportPane } from "./components/ReportPane.js";
import { HelpOverlay } from "./components/HelpOverlay.js";
import { computeLayout } from "./components/Layout.js";
import { useTerminalSize } from "./state/useTerminalSize.js";
import { computePlanRows } from "./components/planRows.js";
import { buildDisplayLines, findMatchIndices } from "./components/AgentPane.js";
import { attachMouseHandler, type MouseEvent as FaceMouseEvent } from "./input/mouse.js";
import { isMouseSequence } from "./input/mouse.js";
import type { AppState, PaneId } from "./state/store.js";
import type { ControlVerb } from "./api/types.js";

export function App({ source }: { source: DataSource }) {
  const app = useAppState();
  const store = useStoreInstance();
  const { exit } = useApp();
  const { stdin } = useStdin();
  const { columns, rows } = useTerminalSize();
  const layout = computeLayout(columns, rows);

  const appRef = useRef<AppState>(app);
  appRef.current = app;

  useEffect(() => {
    const stop = startConnection(store, source);
    return stop;
  }, [source, store]);

  // Mouse routing: attached once, reads live state via appRef so the listener never goes stale.
  useEffect(() => {
    const stop = attachMouseHandler(stdin as NodeJS.ReadStream, (e) => handleMouse(e, appRef.current, store));
    return stop;
  }, [stdin, store, exit]);

  useInput(
    (input, key) => {
      if (isMouseSequence(input)) return;
      if (key.ctrl && input.toLowerCase() === "c") {
        exit();
        return;
      }

      const { ui, planState, processes } = app;

      if (ui.modal !== "none") return; // the open modal owns input entirely while it's up

      if (ui.agentSearchActive) {
        if (key.escape) {
          store.setUi({ agentSearchActive: false, agentSearch: null, agentSearchMatchIdx: -1 });
          return;
        }
        if (key.return) {
          store.setUi({ agentSearchActive: false });
          return;
        }
        if (key.backspace || key.delete) {
          store.setUi({ agentSearch: (ui.agentSearch ?? "").slice(0, -1) });
          return;
        }
        if (input && input.charCodeAt(0) >= 0x20) {
          store.setUi({ agentSearch: (ui.agentSearch ?? "") + input });
        }
        return;
      }

      if (key.tab) {
        const order: PaneId[] = ["plan", "agent", "process"];
        const next = order[(order.indexOf(ui.focusedPane) + 1) % order.length]!;
        store.setUi({ focusedPane: next });
        return;
      }
      if (input === "1") return void store.setUi({ focusedPane: "plan" });
      if (input === "2") return void store.setUi({ focusedPane: "agent" });
      if (input === "3") return void store.setUi({ focusedPane: "process" });
      if (input === ":" || (key.ctrl && input.toLowerCase() === "k")) return void store.setUi({ modal: "palette" });
      if (input === "i") return void store.setUi({ modal: "inject" });
      if (input === "e") return void store.setUi({ modal: "promptEditor" });
      if (input === "h") return void store.setUi({ modal: "sessionHistory" });
      if (input === "r") return void store.setUi({ modal: "report" });
      if (input === "?") return void store.setUi({ modal: "help" });
      if (input === "q") return void exit();

      if (ui.focusedPane === "plan" && planState) {
        const planRows = computePlanRows(planState.stages, planState.stageId, ui.expandedOverrides);
        if (key.upArrow || input === "k") return void store.setUi({ planSelected: Math.max(0, ui.planSelected - 1) });
        if (key.downArrow || input === "j")
          return void store.setUi({ planSelected: Math.min(Math.max(0, planRows.length - 1), ui.planSelected + 1) });
        const row = planRows[ui.planSelected];
        if (key.return && row?.kind === "stage") {
          store.toggleStageExpanded(row.stage.id, row.stage.id === planState.stageId);
          return;
        }
        if (key.rightArrow && row?.kind === "stage") {
          store.toggleStageExpanded(row.stage.id, row.stage.id === planState.stageId);
          return;
        }
        if (key.leftArrow) {
          const stageId = row?.stage.id;
          if (stageId) {
            store.setUi({ expandedOverrides: { ...ui.expandedOverrides, [stageId]: false } });
            const idx = planRows.findIndex((r) => r.kind === "stage" && r.stage.id === stageId);
            if (idx >= 0) store.setUi({ planSelected: idx });
          }
          return;
        }
        return;
      }

      if (ui.focusedPane === "agent") {
        const pageSize = Math.max(1, layout.agent.height - 4);
        if (key.pageUp) return void store.setUi({ agentAutoScroll: false, agentOffset: ui.agentOffset + pageSize });
        if (key.pageDown) {
          const next = Math.max(0, ui.agentOffset - pageSize);
          return void store.setUi({ agentOffset: next, agentAutoScroll: next === 0 });
        }
        if (input === "l") return void store.setUi({ agentAutoScroll: true, agentOffset: 0 });
        if (input === "/") return void store.setUi({ agentSearchActive: true, agentSearch: "", agentSearchMatchIdx: -1 });
        if (input === "f") return void store.setUi({ agentFoldTools: !ui.agentFoldTools });
        if ((input === "n" || input === "N") && ui.agentSearch) {
          jumpToMatch(app, store, input === "n" ? 1 : -1, layout.agent.height - 4);
          return;
        }
        return;
      }

      if (ui.focusedPane === "process") {
        if (key.upArrow) return void store.setUi({ processSelected: Math.max(0, ui.processSelected - 1) });
        if (key.downArrow) return void store.setUi({ processSelected: Math.min(Math.max(0, processes.length - 1), ui.processSelected + 1) });
      }
    },
    { isActive: true },
  );

  const currentStageId = app.planState?.stageId ?? "";
  const planDir = app.planState?.planDir ?? ".";

  return (
    <>
      <Layout />
      <CommandPalette
        visible={app.ui.modal === "palette"}
        currentStageId={currentStageId}
        onExecute={(command: ControlVerb, opts) => runControl(source, store, command, opts)}
        onClose={() => store.setUi({ modal: "none" })}
        width={Math.min(64, columns - 4)}
        height={Math.min(20, rows - 4)}
      />
      <InjectEditor
        visible={app.ui.modal === "inject"}
        currentStageId={currentStageId}
        onSubmit={(content, stageId) => runInject(source, store, content, stageId)}
        onClose={() => store.setUi({ modal: "none" })}
        width={Math.min(80, columns - 4)}
        height={Math.min(24, rows - 4)}
      />
      <PromptEditor
        visible={app.ui.modal === "promptEditor"}
        planDir={planDir}
        onSaved={(path) => store.toast(`saved ${path}`, "success")}
        onClose={() => store.setUi({ modal: "none" })}
        width={Math.min(90, columns - 4)}
        height={Math.min(28, rows - 4)}
      />
      <SessionHistory
        visible={app.ui.modal === "sessionHistory"}
        sessions={app.sessions}
        transcript={app.transcript}
        onClose={() => store.setUi({ modal: "none" })}
        width={Math.min(90, columns - 4)}
        height={Math.min(26, rows - 4)}
      />
      <ReportPane
        visible={app.ui.modal === "report"}
        result={app.reportResult}
        loading={app.reportLoading}
        onRun={(sql) => runQuery(source, store, sql)}
        onClose={() => store.setUi({ modal: "none" })}
        width={Math.min(90, columns - 4)}
        height={Math.min(24, rows - 4)}
      />
      <HelpOverlay
        visible={app.ui.modal === "help"}
        onClose={() => store.setUi({ modal: "none" })}
        width={Math.min(84, columns - 4)}
        height={Math.min(26, rows - 4)}
        mode={app.connection.mode}
        connectionUrl={app.connection.url}
      />
    </>
  );
}

function jumpToMatch(app: ReturnType<typeof useAppState>, store: ReturnType<typeof useStoreInstance>, dir: 1 | -1, viewportHeight: number) {
  const { ui, transcript } = app;
  if (!ui.agentSearch) return;
  const lines = buildDisplayLines(transcript, ui.agentFoldTools);
  const matches = findMatchIndices(lines, ui.agentSearch);
  if (matches.length === 0) return;
  const nextIdx = ((ui.agentSearchMatchIdx < 0 ? 0 : ui.agentSearchMatchIdx + dir) + matches.length) % matches.length;
  const lineIdx = matches[nextIdx]!;
  const offset = Math.max(0, lines.length - 1 - lineIdx - Math.floor(viewportHeight / 2));
  store.setUi({ agentSearchMatchIdx: nextIdx, agentAutoScroll: false, agentOffset: offset });
}

async function runControl(
  source: DataSource,
  store: ReturnType<typeof useStoreInstance>,
  command: ControlVerb,
  opts?: { stageId?: string; force?: boolean; confirmed?: boolean },
) {
  try {
    const res = await source.postControl({ command, ...opts });
    store.toast(res.accepted ? `${command} accepted` : `${command} rejected: ${res.reason ?? "unknown reason"}`, res.accepted ? "success" : "error");
  } catch (err) {
    store.toast(`${command} failed: ${(err as Error).message}`, "error");
  }
}

async function runInject(source: DataSource, store: ReturnType<typeof useStoreInstance>, content: string, stageId: string) {
  try {
    const res = await source.postInject(content, stageId || undefined);
    store.toast(res.accepted ? "injection recorded (not yet auto-applied — F8 scope)" : `inject rejected: ${res.reason}`, res.accepted ? "success" : "error");
    if (res.accepted) store.setUi({ modal: "none" });
  } catch (err) {
    store.toast(`inject failed: ${(err as Error).message}`, "error");
  }
}

async function runQuery(source: DataSource, store: ReturnType<typeof useStoreInstance>, sql: string) {
  store.setReport(true, null);
  try {
    const result = await source.query(sql);
    store.setReport(false, result);
  } catch (err) {
    store.setReport(false, { columns: [], rows: [], truncated: false, error: (err as Error).message });
  }
}

function handleMouse(e: FaceMouseEvent, app: AppState, store: ReturnType<typeof useStoreInstance>) {
  const { ui, planState, processes } = app;
  if (ui.modal !== "none") return; // modals don't participate in pane click-routing

  const regions = ui.regions;
  const inside = (pane: PaneId) => {
    const r = regions[pane];
    return !!r && e.x >= r.x && e.x < r.x + r.width && e.y >= r.y && e.y < r.y + r.height;
  };

  if (e.button === "wheelUp" || e.button === "wheelDown") {
    if (inside("agent")) {
      if (e.button === "wheelUp") store.setUi({ agentAutoScroll: false, agentOffset: ui.agentOffset + 3 });
      else {
        const next = Math.max(0, ui.agentOffset - 3);
        store.setUi({ agentOffset: next, agentAutoScroll: next === 0 });
      }
    }
    return;
  }

  if (e.button !== 0) return; // only left-click selects/focuses

  if (inside("plan")) {
    store.setUi({ focusedPane: "plan" });
    if (planState) {
      const rect = regions.plan!;
      const contentRow = e.y - rect.y - 1; // -1 for the top border
      const planRows = computePlanRows(planState.stages, planState.stageId, ui.expandedOverrides);
      if (contentRow >= 0 && contentRow < planRows.length) store.setUi({ planSelected: contentRow });
    }
    return;
  }
  if (inside("agent")) {
    store.setUi({ focusedPane: "agent" });
    return;
  }
  if (inside("process")) {
    store.setUi({ focusedPane: "process" });
    const rect = regions.process!;
    const contentRow = e.y - rect.y - 1;
    if (contentRow >= 0 && contentRow < processes.length) store.setUi({ processSelected: contentRow });
    return;
  }
}
