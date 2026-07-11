import React from "react";
import { Box, Text, useInput } from "ink";
import { Modal } from "./Modal.js";
import { colors } from "../theme.js";
import { AGENT_PANE_KEYS, GLOBAL_KEYS, PLAN_PANE_KEYS, PROCESS_PANE_KEYS, type KeyHelp } from "../input/keymap.js";

export interface HelpOverlayProps {
  visible: boolean;
  onClose(): void;
  width: number;
  height: number;
  mode: "live" | "demo";
  connectionUrl: string;
}

function KeyList({ title, keys }: { title: string; keys: KeyHelp[] }) {
  return (
    <Box flexDirection="column" marginBottom={1}>
      <Text bold color={colors.accent}>
        {title}
      </Text>
      {keys.map((k) => (
        <Text key={k.key}>
          <Text color={colors.warn}>{k.key.padEnd(14)}</Text>
          {k.description}
        </Text>
      ))}
    </Box>
  );
}

export function HelpOverlay({ visible, onClose, width, height, mode, connectionUrl }: HelpOverlayProps) {
  useInput(
    (_input, key) => {
      if (key.escape || _input === "?") onClose();
    },
    { isActive: visible },
  );

  if (!visible) return null;

  return (
    <Modal title="CONDUCTOR FACE — HELP" width={width} height={height} footer="Esc or ? to close">
      <Box flexDirection="column">
        <Text color={colors.dim}>
          mode: <Text color={mode === "demo" ? colors.accent : colors.ok}>{mode}</Text> · {connectionUrl}
        </Text>
        <Box marginTop={1} flexDirection="row">
          <Box flexDirection="column" width={40} marginRight={2}>
            <KeyList title="Global" keys={GLOBAL_KEYS} />
            <KeyList title="Plan pane" keys={PLAN_PANE_KEYS} />
          </Box>
          <Box flexDirection="column" width={40}>
            <KeyList title="Agent pane" keys={AGENT_PANE_KEYS} />
            <KeyList title="Process pane" keys={PROCESS_PANE_KEYS} />
          </Box>
        </Box>
        <Text color={colors.dim}>Mouse: click a pane to focus it, click a row to select it, scroll wheel to scroll the agent pane.</Text>
      </Box>
    </Modal>
  );
}
