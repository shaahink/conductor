#!/usr/bin/env python3
"""CH3.2 - every reference a reader would follow, resolved against the disk.

Four kinds of reference, because they rot independently:

  markdown   [text](target) and [text]: target in any .md
  json       every string value in .conductor/contracts/*.json and every
             plans/**/*.plan.json that looks like a repo path
  test       every repo path written into a C# or Go string literal, which is
             where a failure MESSAGE tells the next reader where to look
  plan       a plan's tracker / planDoc / readOrder / notes

Two zones, and the difference is the whole point:

  LIVE    a broken reference is a defect and gets fixed
  FROZEN  the run artifacts under .conductor/ (evidence, handovers, sessions,
          attempts, audits, logs) are a RECORD of what a session saw at the
          time. A broken path in one is reported and never rewritten.

Usage:  python tools/ch3/link-sweep.py [--all] [--zone live|frozen]
        --all also prints references that resolve.
"""
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

MD_INLINE = re.compile(r"\[[^\]\n]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
MD_REF = re.compile(r"^\s*\[[^\]\n]+\]:\s*(\S+)", re.M)
# A repo path in a string literal: at least one slash, a known extension.
PATHISH = re.compile(
    r"[\"'`]((?:\.{0,2}/)?(?:[\w.\-]+/)+[\w.\-]+"
    r"\.(?:md|json|cs|go|ps1|sh|py|yml|yaml|tape|gif|slnx|targets|db))[\"'`]")

STRING_LIT = re.compile('"((?:[^"\\\\n]|\\\\.)*)"')
PATHISH_BARE = re.compile(
    r"((?:\.{0,2}/)?(?:[\w.\-]+/)+[\w.\-]+"
    r"\.(?:md|json|cs|csproj|go|ps1|sh|py|yml|yaml|tape|gif|slnx|sln|targets|db)(?![\w.]))")

SKIP_DIRS = {".git", "node_modules", "bin", "obj", "__pycache__", ".vs", "dist"}
# THE RULE (CH3.2, written into docs/dev/README.md): a path is rewritten if and
# only if something still READS it.
#
#   LIVE    the plan in flight, and every document a person or a session is
#           pointed at today. A broken reference here is a defect.
#   FROZEN  the record - a closed era's plan, contracts and tracker, docs/history/,
#           ci-health/, and everything the engine wrote under .conductor/. These
#           say what was true when they were written. The sweep REPORTS them and
#           never rewrites one, because rewriting a record is falsifying it.
FROZEN_TREES = (".conductor", os.path.join("docs", "history"), "ci-health")
# An example plan describes ANOTHER repository's layout on purpose.
OUT_OF_SCOPE_TREES = ("examples",)
DATED = re.compile(r"-\d{4}-\d{2}-\d{2}[a-z-]*\.md$")


def live_plan_dir():
    """The plan directory still in flight. Its TRACKER.md is the one the engine
    rewrites every heartbeat, so mtime picks it out without a hand-kept name."""
    plans = os.path.join(ROOT, "plans")
    best, when = None, -1
    if os.path.isdir(plans):
        for name in os.listdir(plans):
            t = os.path.join(plans, name, "TRACKER.md")
            if os.path.isfile(t) and os.path.getmtime(t) > when:
                best, when = name, os.path.getmtime(t)
    return os.path.join("plans", best) if best else None


LIVE_PLAN = live_plan_dir()


def read_order():
    """The documents the plan in flight tells each session to read. A dated brief
    is a record UNLESS it is one of these - then it is read every session."""
    if not LIVE_PLAN:
        return ()
    out = []
    for name in os.listdir(os.path.join(ROOT, LIVE_PLAN)):
        if not name.endswith(".plan.json"):
            continue
        try:
            with open(os.path.join(ROOT, LIVE_PLAN, name), encoding="utf-8") as fh:
                doc = json.load(fh)
        except (OSError, ValueError):
            continue
        for stage in doc.get("stages", []):
            for t in stage.get("readOrder", []) or []:
                out.append(os.path.normpath(t))
        for t in doc.get("readOrder", []) or []:
            out.append(os.path.normpath(t))
    return tuple(out)


READ_ORDER = read_order()


def zone_of(rel):
    if rel.startswith(FROZEN_TREES):
        return "frozen"
    if rel.startswith("plans" + os.sep):
        return "live" if LIVE_PLAN and rel.startswith(LIVE_PLAN + os.sep) else "frozen"
    if rel.startswith(os.path.join("docs", "dev")):
        # docs/README.md calls this tree "ADRs, findings, backlog, and the pointer
        # to the era in flight". Only the last two are read today: an ADR states a
        # decision as it was made and a finding states what was measured on a date,
        # and both are falsified by being brought up to date.
        live = (os.path.join("docs", "dev", "README.md"),
                os.path.join("docs", "dev", "NEXT-FEATURES.md"))
        return "live" if rel in live or rel in READ_ORDER else "frozen"
    return "live"


def walk(exts):
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            if name.lower().endswith(exts):
                full = os.path.join(base, name)
                rel = os.path.relpath(full, ROOT)
                if rel.startswith(OUT_OF_SCOPE_TREES):
                    continue
                yield rel, full


def external(t):
    # A leading slash here is a home-relative path whose ~ fell outside the
    # backtick (`~/.config/opencode/opencode.json`), not a repo path.
    return (t.startswith(("http://", "https://", "mailto:", "#", "<", "/"))
            or ":" in t.split("/")[0])


def runtime_artifact(t):
    """A `.conductor/...` path in prose is an artifact the engine MAY write, and
    whether it is on disk depends on run state, not on whether the doc is right -
    `.conductor/control.json` is real (CtlCommand.cs:34) and exists only while an
    intent is queued. Whether the docs name the right artifacts is a different
    question, and SF7_1DocsMatchRealityTests already owns it."""
    return t.startswith(".conductor/") or t.startswith(".conductor" + os.sep)


def resolve(src_rel, target):
    """Where a reference points, or None when it is not ours to check."""
    target = target.split("#")[0].split("?")[0].strip()
    if not target or external(target):
        return None
    if target.startswith("/"):
        return os.path.normpath(os.path.join(ROOT, target.lstrip("/")))
    return os.path.normpath(os.path.join(ROOT, os.path.dirname(src_rel), target))


# ARCHITECTURE.md and AGENTS.md cite a file the way a person inside that
# assembly says it - `Orchestration/RunLoop.cs`, not the four-segment path. The
# abbreviation is the house idiom, so resolve a prose path under the repo root
# OR under any assembly root before calling it broken.
# A shape, not a path: session-NNN.json, conductor-YYYYMMDD.json, tests/.../X.cs,
# session-011..013.json. Resolving one says nothing about whether the doc is right.
PLACEHOLDER = re.compile(r"NNN|YYYY|MM-DD|\.\.\.|<|>|\*|\{|\.\.[a-z0-9]")

# People cite a file the way they SAY it, not the way the filesystem spells it:
# `Orchestration/RunLoop.cs`, `Tasks/TaskSplitDtos.cs`, `divan/core.plan.json`.
# Every one of those is a real file's path SUFFIX, so index the repo by basename
# and match on suffix rather than keeping a list of roots that would itself rot.
# `Core/` is the one alias the house uses for the Conductor.Core assembly.
_INDEX = {}


def _index():
    if _INDEX:
        return _INDEX
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            rel = os.path.relpath(os.path.join(base, name), ROOT).replace(os.sep, "/")
            _INDEX.setdefault(name, []).append(rel)
    return _INDEX


def anywhere(src_rel, target):
    """Where a prose path points, or None when nothing in the repo carries it."""
    if target.startswith("./") or target.startswith("../"):
        return resolve(src_rel, target)
    here = os.path.dirname(src_rel).replace(os.sep, "/")
    for candidate in (target, (here + "/" + target).lstrip("/"),
                      target.replace("Core/", "Conductor.Core/", 1)):
        for full in _index().get(candidate.rsplit("/", 1)[-1], ()):
            if full == candidate or full.endswith("/" + candidate):
                return os.path.join(ROOT, full.replace("/", os.sep))
    return os.path.normpath(os.path.join(ROOT, target))


def strings(node):
    """Every string value in a parsed JSON document."""
    if isinstance(node, str):
        yield node
    elif isinstance(node, dict):
        for v in node.values():
            yield from strings(v)
    elif isinstance(node, list):
        for v in node:
            yield from strings(v)


def line_of(raw, needle):
    i = raw.find(needle)
    return raw.count(chr(10), 0, i) + 1 if i >= 0 else 0


def ignored():
    """Paths quoted AS DATA rather than followed. One per line as
    `<source>:<target>  # reason`; the reason is printed with the count, so an
    entry has to justify itself to whoever reads the sweep next."""
    out = {}
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sweep-ignore.txt")
    if not os.path.exists(path):
        return out
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            entry, _, reason = line.partition("#")
            src, _, target = entry.strip().partition(":")
            out[(os.path.normpath(src), target.strip())] = reason.strip()
    return out


IGNORED = ignored()


def redirects(broken):
    """old -> new for every broken reference whose basename exists elsewhere.
    This is what a where-it-went table is made of, and deriving it beats keeping
    one by hand: the table cannot drift from the moves it describes."""
    out = {}
    for _, _, _, target in broken:
        for full in _index().get(target.rsplit("/", 1)[-1], ()):
            out.setdefault(target, set()).add(full)
    return out


def collect():
    """(kind, source, lineno, target, absolute) for every reference found."""
    refs = []

    for rel, full in walk((".md",)):
        with open(full, encoding="utf-8", errors="replace") as fh:
            text = fh.read()
        offsets = [0]
        for line in text.splitlines(True):
            offsets.append(offsets[-1] + len(line))
        def lineno(pos):
            lo, hi = 0, len(offsets) - 1
            while lo < hi - 1:
                mid = (lo + hi) // 2
                if offsets[mid] <= pos:
                    lo = mid
                else:
                    hi = mid
            return lo + 1
        for pat in (MD_INLINE, MD_REF):
            for m in pat.finditer(text):
                t = m.group(1)
                a = resolve(rel, t)
                if a:
                    refs.append(("markdown", rel, lineno(m.start()), t, a))

    for rel, full in walk((".cs", ".go")):
        # The mandate is a path named in a test MESSAGE - the sentence a failing
        # assertion prints at the next reader. A doc comment in production code
        # is prose and is swept as prose, not here.
        if not (rel.endswith("Tests.cs") or rel.endswith("_test.go")):
            continue
        with open(full, encoding="utf-8", errors="replace") as fh:
            body = fh.read()
        # Every bare path literal in the file: what the test BUILDS. `src/Foo.cs`
        # written into a temp directory is a fixture, and an assertion quoting it
        # back is quoting the fixture, not pointing into this repo.
        fixtures = {lit.strip() for lit in STRING_LIT.findall(body) if " " not in lit.strip()}
        fixtures |= {os.path.basename(f) for f in fixtures}
        # ...and anything the file mentions MORE THAN ONCE. A path a test moves
        # through itself appears twice - once as the input it builds, once in the
        # assertion that reads it back ("M src/Foo.cs" is git output, not a file).
        # A pointer at the next reader is written once, where the failure prints.
        counts = {}
        for lit in STRING_LIT.findall(body):
            for m in PATHISH_BARE.finditer(lit):
                counts[m.group(1)] = counts.get(m.group(1), 0) + 1
        fixtures |= {t for t, c in counts.items() if c > 1}
        for n, line in enumerate(body.splitlines(), 1):
            if line.lstrip().startswith(("//", "///", "*")):
                continue
            for lit in STRING_LIT.findall(line):
                if " " not in lit.strip():
                    continue          # a bare path constant, not a message
                for m in PATHISH_BARE.finditer(lit):
                    t = m.group(1)
                    if (PLACEHOLDER.search(t) or external(t) or runtime_artifact(t)
                            or t in fixtures or os.path.basename(t) in fixtures):
                        continue
                    refs.append(("message", rel, n, t, anywhere(rel, t)))

    # A plan's `notes` and a contract's lane_notes are one long prose STRING, so
    # the paths they cite are not quote-delimited. Parse the document and read
    # every string value instead of matching on quotes.
    for rel, full in walk((".json",)):
        if not (rel.startswith(os.path.join(".conductor", "contracts"))
                or rel.startswith("plans" + os.sep) or rel.endswith(".plan.json")):
            continue
        with open(full, encoding="utf-8", errors="replace") as fh:
            raw = fh.read()
        try:
            doc = json.loads(raw)
        except ValueError:
            continue
        kind = "plan" if rel.endswith(".plan.json") else "contract"
        seen = set()
        for value in strings(doc):
            for m in PATHISH_BARE.finditer(value):
                t = m.group(1).rstrip(".,;:)")
                if t in seen or PLACEHOLDER.search(t) or external(t) or runtime_artifact(t):
                    continue
                seen.add(t)
                refs.append((kind, rel, line_of(raw, t), t, anywhere(rel, t)))

    # Prose in markdown: `docs/dev/README.md` in backticks is a reference a
    # reader follows exactly as hard as a link, and it is the form the plans'
    # notes and this repo's own traps are written in.
    for rel, full in walk((".md",)):
        with open(full, encoding="utf-8", errors="replace") as fh:
            for n, line in enumerate(fh, 1):
                for m in re.finditer(r"`([^`\n]+)`", line):
                    for pm in PATHISH_BARE.finditer(m.group(1)):
                        t = pm.group(1)
                        if PLACEHOLDER.search(t) or runtime_artifact(t) or external(t):
                            continue
                        a = anywhere(rel, t)
                        if a:
                            refs.append(("prose", rel, n, t, a))

    return refs


def main():
    show_all = "--all" in sys.argv
    want = None
    if "--zone" in sys.argv:
        want = sys.argv[sys.argv.index("--zone") + 1]

    refs = collect()
    broken = {"live": [], "frozen": []}
    ok = 0
    excused = 0
    for kind, src, n, target, absolute in refs:
        if os.path.exists(absolute):
            ok += 1
            if show_all:
                print("  OK   %s:%d -> %s" % (src, n, target))
            continue
        if (os.path.normpath(src), target) in IGNORED:
            excused += 1
            continue
        broken[zone_of(src)].append((kind, src, n, target))

    for zone in ("live", "frozen"):
        if want and zone != want:
            continue
        rows = sorted(broken[zone])
        head = {"live": "LIVE - a broken reference here is a defect",
                "frozen": "FROZEN - the run's own record; REPORTED, never rewritten"}[zone]
        print("== %s == %d broken" % (head, len(rows)))
        for kind, src, n, target in rows:
            print("  [%s] %s:%d -> %s" % (kind, src, n, target))
        print()

    if "--redirects" in sys.argv:
        print("== WHERE IT WENT - derived, not hand-kept ==")
        for old, news in sorted(redirects(broken["frozen"] + broken["live"]).items()):
            print("  %s -> %s" % (old, ", ".join(sorted(news))))
        print()

    for (src, target), reason in sorted(IGNORED.items()):
        print("  excused  %s -> %s  (%s)" % (src, target, reason))

    print("== counts == checked=%d resolved=%d excused=%d live-broken=%d frozen-broken=%d"
          % (len(refs), ok, excused, len(broken["live"]), len(broken["frozen"])))
    return 1 if broken["live"] else 0


if __name__ == "__main__":
    sys.exit(main())
