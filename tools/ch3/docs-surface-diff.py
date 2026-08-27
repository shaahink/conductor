#!/usr/bin/env python3
"""CH3.1 - reconcile the published docs against a real conductor binary.

Mechanical, both directions:
  STALE      a flag the docs name for a verb that the binary does not parse
  UNDOCUMENTED a flag the binary parses that docs/cli.md never names for that verb

Usage:  python tools/ch3/docs-surface-diff.py <help-dump-dir> [doc ...]

The help dump is produced by tools/ch3/dump-help.ps1 <binary> <dir>: one
<verb>.txt per top-level verb, with every sub-verb's help appended to it.
"""
import os
import re
import sys

FLAG = re.compile(r"--[a-z][a-z0-9-]*")
GLOBAL = {"--help", "--version", "--plan", "--json", "--yes", "--force"}
# Flags of the AGENT CLI a plan drives, written in prose that names no
# foreign command. No conductor verb declares them and none ever will, so
# they are excused by name rather than left to make the exit code lie.
AGENT_CLI = {"--permission-mode", "--dangerously-skip-permissions"}
# Other people's CLIs. A line that drives one of these is quoting its flags,
# not conductor's, so it is out of scope rather than stale.
FOREIGN = re.compile(
    r"\b(git|dotnet|claude|opencode|codex|gemini|npm|npx|gh|docker|vhs|pwsh|"
    r"powershell|stryker|deepseek|curl|cargo|python|node)\b")
# A flag inside a markdown link target is an anchor, not a flag.
LINK = re.compile(r"\]\([^)]*\)")


def options_only(text):
    """Flags the parser DECLARES, not flags its prose happens to quote.

    A verb's description quotes other verbs' flags - `journey` names
    `conductor run [--paused]` - so scanning the whole help attributes --paused
    to journey and the diff then reports a gap that is not one.
    """
    flags = set()
    keeping = False
    for line in text.splitlines():
        if re.match(r"^[A-Z][A-Z ]+:\s*$", line):
            keeping = line.startswith("OPTIONS")
            continue
        if keeping:
            m = re.match(r"^\s+(?:-[a-zA-Z], )?(--[a-z][a-z0-9-]*)", line)
            if m:
                flags.add(m.group(1))
    return flags


def load_help(d):
    surface = {}
    for name in os.listdir(d):
        if not name.endswith(".txt"):
            continue
        verb = name[:-4]
        with open(os.path.join(d, name), encoding="utf-8", errors="replace") as fh:
            surface[verb] = options_only(fh.read())
    return surface


def verbs_named(line, known):
    """Every known verb this line puts in scope."""
    found = set()
    for m in re.finditer(r"\bconductor\s+([a-z][a-z0-9-]*)", line):
        if m.group(1) in known:
            found.add(m.group(1))
    for m in re.finditer(r"`([a-z][a-z0-9-]*)", line):
        if m.group(1) in known:
            found.add(m.group(1))
    # This page lists a verb's own help as a fenced block that OPENS with the
    # bare verb and two spaces. Scope it to that verb, or the prose after the
    # options ("attach later with `face`") wins and every option reads misplaced.
    head = re.match(r"([a-z][a-z0-9-]*)\s\s+", line)
    if head and head.group(1) in known:
        return {head.group(1)}
    return found


def blocks(path):
    """The document as units of meaning: one table ROW is a block of its own
    (each row is a different verb), and every other run of consecutive
    non-blank lines is one paragraph block. Yields [(lineno, text), ...]."""
    out, cur, fenced = [], [], False
    with open(path, encoding="utf-8", errors="replace") as fh:
        for n, line in enumerate(fh, 1):
            line = line.rstrip()
            if line.lstrip().startswith("```"):
                if cur:
                    out.append(cur)
                    cur = []
                fenced = not fenced
                continue
            if not fenced and line.lstrip().startswith("|"):
                if cur:
                    out.append(cur)
                    cur = []
                out.append([(n, line)])
            elif line.strip():
                cur.append((n, line))
            elif cur:
                out.append(cur)
                cur = []
    if cur:
        out.append(cur)
    return out


def main():
    help_dir = sys.argv[1]
    docs = sys.argv[2:]
    surface = load_help(help_dir)
    known = set(surface)
    every_flag = set().union(*surface.values()) if surface else set()

    stale = []
    misplaced = []
    doc_flags_by_verb = {v: set() for v in known}

    for doc in docs:
        for block in blocks(doc):
            # Scope is the whole block: a paragraph is one unit of meaning, and
            # the verb a sentence is about is often on the line above the flag.
            text = " ".join(line for _, line in block)
            scope = verbs_named(text, known)
            accepted = set()
            block_flags = set(FLAG.findall(LINK.sub("", text))) - GLOBAL - AGENT_CLI
            for v in scope:
                accepted |= surface[v]
                doc_flags_by_verb[v] |= block_flags
            if FOREIGN.search(text):
                continue
            for n, line in block:
                # A line that drives another CLI still DOCUMENTS the conductor
                # flags it names, so it was credited above; it is only excused
                # the judgement below, where its foreign flags read as ours.
                if FOREIGN.search(line):
                    continue
                for f in sorted(set(FLAG.findall(LINK.sub("", line))) - GLOBAL - AGENT_CLI):
                    if f in accepted or not scope:
                        continue  # no conductor verb in scope: not ours to judge
                    row = (doc, n, f, ",".join(sorted(scope)), line.strip()[:110])
                    (stale if f not in every_flag else misplaced).append(row)

    def show(rows):
        if not rows:
            print("  (none)")
        for doc, n, f, where, text in rows:
            print("  %s:%d  %s  [verb in scope: %s]" % (doc, n, f, where))
            print("      %s" % text)

    print("== STALE: the docs write a flag NO conductor verb declares ==")
    show(stale)

    print()
    print("== MISPLACED: some verb declares it, but not the one this line names ==")
    show(misplaced)

    print()
    print("== UNDOCUMENTED: binary parses a flag no doc names for that verb ==")
    any_missing = False
    for v in sorted(known):
        missing = sorted((surface[v] - GLOBAL) - doc_flags_by_verb[v])
        if missing:
            any_missing = True
            print("  %-16s %s" % (v, " ".join(missing)))
    if not any_missing:
        print("  (none)")

    print()
    print("== counts == verbs=%d stale=%d misplaced=%d ==" % (
        len(known), len(stale), len(misplaced)))
    return 1 if stale else 0


if __name__ == "__main__":
    sys.exit(main())
