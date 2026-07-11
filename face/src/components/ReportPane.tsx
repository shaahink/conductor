import React, { useState } from "react";
import { Box, Text, useInput } from "ink";
import { Modal } from "./Modal.js";
import { TextInput } from "./TextInput.js";
import { colors } from "../theme.js";
import { isMouseSequence } from "../input/mouse.js";
import type { QueryResultDto } from "../api/types.js";

export interface ReportPaneProps {
  visible: boolean;
  result: QueryResultDto | null;
  loading: boolean;
  onRun(sql: string): void;
  onClose(): void;
  width: number;
  height: number;
}

const QUICK_QUERIES: Array<{ label: string; sql: string }> = [
  { label: "cost per stage", sql: "SELECT stage_id, SUM(cost_usd) as cost_usd FROM costs GROUP BY stage_id ORDER BY cost_usd DESC" },
  { label: "which gates fail most", sql: "SELECT name, COUNT(*) as failures FROM gates WHERE passed = 0 GROUP BY name ORDER BY failures DESC" },
  { label: "recent sessions", sql: "SELECT number, stage_id, kind, outcome FROM sessions ORDER BY number DESC LIMIT 20" },
  { label: "verifier scores", sql: "SELECT session_number, score, verdict FROM scores ORDER BY session_number DESC LIMIT 20" },
];

/** F6 embedded reporting (D11): the same ad-hoc SQL surface as `conductor report --query` (F1.4),
 * plus canned quick-queries matching the design doc's own example questions ("cost of stage R3?",
 * "which gates fail most?"). SELECT-only, enforced engine-side too. */
export function ReportPane({ visible, result, loading, onRun, onClose, width, height }: ReportPaneProps) {
  const [sql, setSql] = useState("SELECT stage_id, SUM(cost_usd) as cost_usd FROM costs GROUP BY stage_id");
  const [quickSelected, setQuickSelected] = useState(0);
  const [focusField, setFocusField] = useState<"quick" | "sql">("sql");

  useInput(
    (input, key) => {
      if (isMouseSequence(input)) return;
      if (key.escape) {
        onClose();
        return;
      }
      if (key.tab) {
        setFocusField((f) => (f === "sql" ? "quick" : "sql"));
        return;
      }
      if (focusField === "quick") {
        if (key.upArrow) setQuickSelected((s) => Math.max(0, s - 1));
        if (key.downArrow) setQuickSelected((s) => Math.min(QUICK_QUERIES.length - 1, s + 1));
        if (key.return) {
          const q = QUICK_QUERIES[quickSelected];
          if (q) {
            setSql(q.sql);
            onRun(q.sql);
          }
        }
      }
    },
    { isActive: visible },
  );

  if (!visible) return null;

  return (
    <Modal title="REPORT / QUERY CONSOLE" width={width} height={height} footer="Tab: switch quick-query list / SQL box · Enter: run · Esc: close">
      <Box>
        <Box flexDirection="column" width={26} marginRight={2}>
          <Text color={colors.dim}>quick queries:</Text>
          {QUICK_QUERIES.map((q, i) => (
            <Text key={q.label} backgroundColor={focusField === "quick" && i === quickSelected ? colors.accentDim : undefined}>
              {focusField === "quick" && i === quickSelected ? "▶ " : "  "}
              {q.label}
            </Text>
          ))}
        </Box>
        <Box flexDirection="column" flexGrow={1}>
          <Box borderStyle="single" borderColor={focusField === "sql" ? colors.accent : colors.dim} paddingX={1}>
            <TextInput value={sql} onChange={setSql} onSubmit={onRun} focus={visible && focusField === "sql"} />
          </Box>
          <Box marginTop={1} flexDirection="column">
            {loading && <Text color={colors.dim}>running…</Text>}
            {!loading && result?.error && <Text color={colors.error}>error: {result.error}</Text>}
            {!loading && result && !result.error && <QueryTable result={result} width={width - 32} />}
            {!loading && !result && <Text color={colors.dim}>run a query to see results</Text>}
          </Box>
        </Box>
      </Box>
    </Modal>
  );
}

function QueryTable({ result, width }: { result: QueryResultDto; width: number }) {
  if (result.columns.length === 0) return <Text color={colors.dim}>no rows</Text>;
  const colWidth = Math.max(8, Math.floor(width / Math.max(1, result.columns.length)) - 1);
  return (
    <Box flexDirection="column">
      <Text bold>{result.columns.map((c) => c.padEnd(colWidth).slice(0, colWidth)).join(" ")}</Text>
      {result.rows.slice(0, 15).map((r, i) => (
        <Text key={i} color={colors.dim}>
          {r.values.map((v) => v.padEnd(colWidth).slice(0, colWidth)).join(" ")}
        </Text>
      ))}
      {result.truncated && <Text color={colors.warn}>… truncated</Text>}
    </Box>
  );
}
