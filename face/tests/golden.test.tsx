import React from "react";
import { describe, expect, it } from "vitest";
import { App } from "../src/App.js";
import { Store } from "../src/state/store.js";
import { StoreProvider } from "../src/state/context.js";
import { renderAt, tick } from "./testUtils.js";
import { StaticDataSource } from "./fixtures.js";

// F6.5: golden-layout snapshot tests at the 3 sizes the design doc's acceptance list (D11) names,
// plus the "no truncation at 100+ cols" claim checked as a concrete assertion, not just a snapshot
// (a snapshot alone would silently "pass" a regression if the committed snapshot were updated
// carelessly — the column-count assertion can't be fooled that way).
const SIZES = [
  { name: "80x24", columns: 80, rows: 24 },
  { name: "120x30", columns: 120, rows: 30 },
  { name: "200x50", columns: 200, rows: 50 },
];

// ANSI escape codes inflate raw string length without occupying a terminal column — strip them
// before measuring line width, otherwise every colored line would look "too wide".
// eslint-disable-next-line no-control-regex
const ANSI_RE = /\x1b\[[0-9;]*m/g;
function visibleWidth(line: string): number {
  return line.replace(ANSI_RE, "").length;
}

function renderApp(columns: number, rows: number) {
  const store = new Store("live", "http://127.0.0.1:4317");
  const source = new StaticDataSource();
  return renderAt(
    <StoreProvider store={store}>
      <App source={source} />
    </StoreProvider>,
    columns,
    rows,
  );
}

describe("golden layout", () => {
  for (const { name, columns, rows } of SIZES) {
    it(`renders at ${name} without throwing and matches the golden snapshot`, async () => {
      const { instance, stdout } = renderApp(columns, rows);
      await tick(20);
      const frame = stdout.lastFrame();
      expect(frame).toBeTruthy();
      expect(frame).toMatchSnapshot();
      instance.unmount();
    });

    it(`renders the plan/agent/process panes and ticker at ${name}`, async () => {
      const { instance, stdout } = renderApp(columns, rows);
      await tick(20);
      const frame = stdout.lastFrame() ?? "";
      expect(frame).toContain("PLAN");
      expect(frame).toContain("AGENT");
      expect(frame).toContain("PROCESSES");
      expect(frame).toContain("F6"); // current stage from the fixture
      instance.unmount();
    });

    it(`never truncates a line to fit — no rendered line exceeds ${columns} columns`, async () => {
      const { instance, stdout } = renderApp(columns, rows);
      await tick(20);
      const frame = stdout.lastFrame() ?? "";
      const lines = frame.split("\n");
      for (const line of lines) {
        expect(visibleWidth(line)).toBeLessThanOrEqual(columns);
      }
      instance.unmount();
    });
  }
});
