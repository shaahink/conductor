import React from "react";
import { Text, useInput } from "ink";
import { isMouseSequence } from "../input/mouse.js";
import { colors } from "../theme.js";

export interface TextInputProps {
  value: string;
  onChange(value: string): void;
  onSubmit?(value: string): void;
  focus?: boolean;
  placeholder?: string;
}

/** Single-line input for the palette/query box — cursor always pinned to the end (no mid-string
 * editing needed for a search/filter/SQL box short enough to just retype). */
export function TextInput({ value, onChange, onSubmit, focus = true, placeholder }: TextInputProps) {
  useInput(
    (input, key) => {
      if (isMouseSequence(input)) return;
      if (key.return) {
        onSubmit?.(value);
        return;
      }
      if (key.backspace || key.delete) {
        onChange(value.slice(0, -1));
        return;
      }
      if (key.ctrl && (input === "u" || input === "U")) {
        onChange("");
        return;
      }
      if (!key.ctrl && !key.meta && input && input.length > 0 && input.charCodeAt(0) >= 0x20) {
        onChange(value + input);
      }
    },
    { isActive: focus },
  );

  if (value.length === 0 && placeholder) {
    return <Text color={colors.dim}>{placeholder}</Text>;
  }
  return (
    <Text>
      {value}
      {focus ? <Text color={colors.accent}>▏</Text> : null}
    </Text>
  );
}
