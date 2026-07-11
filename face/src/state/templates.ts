import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";

// The 7 session-lifecycle templates PromptBuilder.Render() looks for under <planDir>/*.md
// (Core/PromptBuilder.cs) — if one isn't on disk, the engine falls back to its own built-in
// default (not duplicated here; editing here always creates/overrides the file on disk).
export const SESSION_TEMPLATES = ["session.md", "fix.md", "resume.md", "advisor.md", "audit.md", "review.md", "verify.md"];

export interface TemplateEntry {
  /** Display name, e.g. "session.md" or "personas/architect.md". */
  label: string;
  /** Absolute path on disk. */
  path: string;
  exists: boolean;
}

/** Lists the known session templates + whatever's in <planDir>/personas — direct filesystem
 * access, no HTTP round-trip: the Face runs on the same machine as the engine (D1), and
 * PromptBuilder/PersonaRegistry already re-read these files from disk on every render, so editing
 * them here needs no new engine primitive (design doc D11). */
export function listTemplates(planDir: string): TemplateEntry[] {
  const entries: TemplateEntry[] = SESSION_TEMPLATES.map((name) => {
    const path = join(planDir, name);
    return { label: name, path, exists: existsSync(path) };
  });

  const personasDir = join(planDir, "personas");
  if (existsSync(personasDir)) {
    try {
      for (const f of readdirSync(personasDir)) {
        if (!f.endsWith(".md")) continue;
        const path = join(personasDir, f);
        entries.push({ label: `personas/${f}`, path, exists: true });
      }
    } catch {
      /* best effort — a permissions error here shouldn't crash the editor list */
    }
  }
  return entries;
}

export function readTemplate(path: string): string {
  if (!existsSync(path)) return "";
  try {
    return readFileSync(path, "utf8");
  } catch {
    return "";
  }
}

export function writeTemplate(path: string, content: string): void {
  const dir = dirname(path);
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
  writeFileSync(path, content, "utf8");
}
