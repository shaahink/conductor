import React, { useState } from "react";
import { Box, Text, useInput } from "ink";
import { Modal } from "./Modal.js";
import { TextInput } from "./TextInput.js";
import { TextEditor } from "./TextEditor.js";
import { colors } from "../theme.js";
import { isMouseSequence } from "../input/mouse.js";

export interface InjectEditorProps {
  visible: boolean;
  currentStageId: string;
  onSubmit(content: string, stageId: string): void;
  onClose(): void;
  width: number;
  height: number;
}

/** F6.4 inject editor (D11: "inject editor with preview"). Recorded to run.db's injections table
 * via POST /inject — visible in reporting immediately; NOT YET threaded into the next session's
 * prompt (that wiring is F8/Telegram scope, documented on the engine side too). */
export function InjectEditor({ visible, currentStageId, onSubmit, onClose, width, height }: InjectEditorProps) {
  const [field, setField] = useState<"stage" | "content">("content");
  const [stageId, setStageId] = useState(currentStageId);
  const [content, setContent] = useState("");

  useInput(
    (input, key) => {
      if (isMouseSequence(input)) return;
      if (key.escape) {
        onClose();
        return;
      }
      if (key.tab) {
        setField((f) => (f === "stage" ? "content" : "stage"));
        return;
      }
      if (key.ctrl && input.toLowerCase() === "s") {
        if (content.trim().length > 0) onSubmit(content, stageId.trim());
        return;
      }
    },
    { isActive: visible },
  );

  if (!visible) return null;

  const editorHeight = Math.max(3, height - 10);

  return (
    <Modal title="INJECT — human note for the next session" width={width} height={height} footer="Tab: switch field · Ctrl+S: record · Esc: cancel">
      <Box flexDirection="column">
        <Box>
          <Text color={colors.dim}>target stage: </Text>
          <Box borderStyle="single" borderColor={field === "stage" ? colors.accent : colors.dim} paddingX={1}>
            <TextInput value={stageId} onChange={setStageId} focus={visible && field === "stage"} placeholder="(run-level)" />
          </Box>
        </Box>
        <Box marginTop={1}>
          <Text color={colors.dim}>content:</Text>
        </Box>
        <Box borderStyle="single" borderColor={field === "content" ? colors.accent : colors.dim} height={editorHeight + 2} paddingX={1}>
          <TextEditor value={content} onChange={setContent} height={editorHeight} width={Math.max(10, width - 8)} focus={visible && field === "content"} placeholder="Type the note to record for the next session…" />
        </Box>
        <Box marginTop={1} flexDirection="column">
          <Text color={colors.dim}>preview — recorded to run.db, not yet auto-injected into a prompt (lands in F8):</Text>
          <Text color={colors.warn}>[injection → {stageId.trim() || "(run-level)"}] {content.split("\n")[0]?.slice(0, 60) || "(empty)"}</Text>
        </Box>
      </Box>
    </Modal>
  );
}
