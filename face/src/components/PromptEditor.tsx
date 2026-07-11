import React, { useMemo, useState } from "react";
import { Box, Text, useInput } from "ink";
import { Modal } from "./Modal.js";
import { TextEditor } from "./TextEditor.js";
import { colors } from "../theme.js";
import { isMouseSequence } from "../input/mouse.js";
import { listTemplates, readTemplate, writeTemplate, type TemplateEntry } from "../state/templates.js";

export interface PromptEditorProps {
  visible: boolean;
  planDir: string;
  onClose(): void;
  onSaved(path: string): void;
  width: number;
  height: number;
}

/** F6.4 templated prompt/persona editor (D11): edits the same files PromptBuilder/PersonaRegistry
 * already hot-reload from disk on every session — no engine round-trip, direct filesystem access
 * (the Face runs on the same machine as the engine, D1). Saving here takes effect at the NEXT
 * session boundary, exactly like hand-editing the file would. */
export function PromptEditor({ visible, planDir, onClose, onSaved, width, height }: PromptEditorProps) {
  const entries = useMemo(() => (visible ? listTemplates(planDir) : []), [visible, planDir]);
  const [selected, setSelected] = useState(0);
  const [editing, setEditing] = useState<TemplateEntry | null>(null);
  const [content, setContent] = useState("");
  const [dirty, setDirty] = useState(false);
  const [savedMsg, setSavedMsg] = useState<string | null>(null);

  useInput(
    (input, key) => {
      if (isMouseSequence(input)) return;

      if (!editing) {
        if (key.escape) {
          onClose();
          return;
        }
        if (key.upArrow) setSelected((s) => Math.max(0, s - 1));
        if (key.downArrow) setSelected((s) => Math.min(Math.max(0, entries.length - 1), s + 1));
        if (key.return) {
          const entry = entries[selected];
          if (entry) {
            setEditing(entry);
            setContent(readTemplate(entry.path));
            setDirty(false);
            setSavedMsg(null);
          }
        }
        return;
      }

      // editing an entry
      if (key.escape) {
        setEditing(null);
        return;
      }
      if (key.ctrl && input.toLowerCase() === "s") {
        writeTemplate(editing.path, content);
        setDirty(false);
        setSavedMsg(`saved ${editing.path}`);
        onSaved(editing.path);
        return;
      }
    },
    { isActive: visible },
  );

  if (!visible) return null;

  if (editing) {
    const editorHeight = Math.max(3, height - 8);
    return (
      <Modal title={`EDIT — ${editing.label}`} width={width} height={height} footer="Ctrl+S: save (engine hot-reloads next session) · Esc: back to list">
        <Box flexDirection="column">
          <Text color={colors.dim}>{editing.path}</Text>
          {!editing.exists && <Text color={colors.warn}>not on disk yet — the engine currently uses its built-in default; saving creates this override</Text>}
          <Box borderStyle="single" borderColor={colors.accent} paddingX={1} marginTop={1} height={editorHeight + 2}>
            <TextEditor
              value={content}
              onChange={(v) => {
                setContent(v);
                setDirty(true);
              }}
              height={editorHeight}
              width={Math.max(10, width - 8)}
              focus={visible}
            />
          </Box>
          {dirty && <Text color={colors.warn}>unsaved changes</Text>}
          {savedMsg && <Text color={colors.ok}>✓ {savedMsg}</Text>}
        </Box>
      </Modal>
    );
  }

  return (
    <Modal title="PROMPT & PERSONA TEMPLATES" width={width} height={height} footer="Enter: edit · Esc: close">
      <Box flexDirection="column">
        <Text color={colors.dim}>{planDir}</Text>
        <Box flexDirection="column" marginTop={1}>
          {entries.map((e, i) => (
            <Text key={e.path} backgroundColor={i === selected ? colors.accentDim : undefined}>
              {i === selected ? "▶ " : "  "}
              {e.label.padEnd(28)}
              <Text color={e.exists ? colors.ok : colors.dim}>{e.exists ? "on disk" : "built-in default"}</Text>
            </Text>
          ))}
        </Box>
      </Box>
    </Modal>
  );
}
