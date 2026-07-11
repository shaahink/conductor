import React, { useMemo, useState } from "react";
import { Box, Text, useInput } from "ink";
import { CONTROL_VERBS, DESTRUCTIVE_VERBS, VERB_DESCRIPTIONS, type ControlVerb } from "../api/types.js";
import { colors } from "../theme.js";
import { isMouseSequence } from "../input/mouse.js";
import { Modal } from "./Modal.js";

export interface CommandPaletteProps {
  visible: boolean;
  currentStageId: string;
  onExecute(command: ControlVerb, opts?: { stageId?: string; force?: boolean; confirmed?: boolean }): void;
  onClose(): void;
  width: number;
  height: number;
}

type Mode = "list" | "confirm" | "gotoStage";

/** F6.4 command palette (D11: replaces the "20-key chord bar"). `:`/Ctrl+K opens it; type to
 * filter the 11 control verbs, Enter to run (destructive verbs get a confirm step, `goto` gets a
 * stage-id prompt) — same semantics whether it drives control.json or the HTTP control plane,
 * since both converge on ControlDispatcher engine-side. */
export function CommandPalette({ visible, currentStageId, onExecute, onClose, width, height }: CommandPaletteProps) {
  const [filter, setFilter] = useState("");
  const [selected, setSelected] = useState(0);
  const [mode, setMode] = useState<Mode>("list");
  const [pendingVerb, setPendingVerb] = useState<ControlVerb | null>(null);
  const [stageInput, setStageInput] = useState(currentStageId);

  const filtered = useMemo(
    () => CONTROL_VERBS.filter((v) => v.includes(filter.toLowerCase()) || VERB_DESCRIPTIONS[v].toLowerCase().includes(filter.toLowerCase())),
    [filter],
  );

  useInput(
    (input, key) => {
      if (isMouseSequence(input)) return;

      if (key.escape) {
        if (mode === "list") onClose();
        else {
          setMode("list");
          setPendingVerb(null);
        }
        return;
      }

      if (mode === "confirm") {
        if (key.return || input.toLowerCase() === "y") {
          if (pendingVerb) onExecute(pendingVerb, { force: true, confirmed: true });
          onClose();
        } else if (input.toLowerCase() === "n") {
          setMode("list");
          setPendingVerb(null);
        }
        return;
      }

      if (mode === "gotoStage") {
        if (key.return) {
          onExecute("goto", { stageId: stageInput.trim() });
          onClose();
          return;
        }
        if (key.backspace || key.delete) {
          setStageInput((s) => s.slice(0, -1));
          return;
        }
        if (input && input.charCodeAt(0) >= 0x20) setStageInput((s) => s + input);
        return;
      }

      // mode === "list"
      if (key.upArrow) {
        setSelected((s) => Math.max(0, s - 1));
        return;
      }
      if (key.downArrow) {
        setSelected((s) => Math.min(Math.max(0, filtered.length - 1), s + 1));
        return;
      }
      if (key.return) {
        const verb = filtered[selected];
        if (!verb) return;
        if (verb === "goto") {
          setMode("gotoStage");
        } else if (DESTRUCTIVE_VERBS.has(verb)) {
          setPendingVerb(verb);
          setMode("confirm");
        } else {
          onExecute(verb);
          onClose();
        }
        return;
      }
      if (key.backspace || key.delete) {
        setFilter((f) => f.slice(0, -1));
        setSelected(0);
        return;
      }
      if (input && input.charCodeAt(0) >= 0x20) {
        setFilter((f) => f + input);
        setSelected(0);
      }
    },
    { isActive: visible },
  );

  if (!visible) return null;

  return (
    <Modal title="COMMAND PALETTE" width={width} height={height}>
      {mode === "confirm" && pendingVerb ? (
        <Box flexDirection="column">
          <Text color={colors.warn}>⚠ {pendingVerb} — {VERB_DESCRIPTIONS[pendingVerb]}</Text>
          <Text>This is destructive. Confirm? (y/Enter to confirm, n/Esc to cancel)</Text>
        </Box>
      ) : mode === "gotoStage" ? (
        <Box flexDirection="column">
          <Text>Jump to stage id:</Text>
          <Text color={colors.accent}>
            {stageInput}
            <Text color={colors.accent}>▏</Text>
          </Text>
          <Text color={colors.dim}>Enter to confirm, Esc to cancel</Text>
        </Box>
      ) : (
        <Box flexDirection="column">
          <Text>
            {"> "}
            {filter}
            <Text color={colors.accent}>▏</Text>
          </Text>
          <Box flexDirection="column" marginTop={1}>
            {filtered.length === 0 && <Text color={colors.dim}>no matching command</Text>}
            {filtered.map((v, i) => (
              <Text key={v} backgroundColor={i === selected ? colors.accentDim : undefined}>
                {i === selected ? "▶ " : "  "}
                <Text bold color={DESTRUCTIVE_VERBS.has(v) ? colors.error : colors.text}>
                  {v.padEnd(20)}
                </Text>
                <Text color={colors.dim}>{VERB_DESCRIPTIONS[v]}</Text>
              </Text>
            ))}
          </Box>
        </Box>
      )}
    </Modal>
  );
}
