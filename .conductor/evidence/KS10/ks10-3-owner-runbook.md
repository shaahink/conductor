# KS10.3 — owner runbook, preconditions measured

Session #25, 2026-08-15. **No step below was performed.** The contract for KS10.3 reads:
*"OWNER-ONLY — no session performs any of this. Contracted here as a handover checklist for the
owner; a delivery agent's only job is to leave the checklist runnable and the tracker row
untouched."* (`plans/karvansara/contracts/KS9-10.json`, KS10.3 acceptance[0].)

So this session did the one job it has: it ran every read-only check the owner's nine steps depend
on, and records below what is already true, what will fail if done out of order, and the two places
where the checklist's own wording points at the wrong file. The tracker row is untouched — KS10.3 is
still TODO, for the owner to mark.

Nothing here writes: no merge, no tag, no install, no GitHub call, no push to payesh.

## Verdict

The checklist is runnable as written, in the order written. Two corrections and one ordering
constraint are recorded below; none of them blocks the owner.

## Step-by-step, with what was measured

### 1. "no other conductor run is live" — RUNNABLE NOW, and it is the one verb that still works

`conductor ps` on the **currently installed** PATH engine (0.4.1-alpha.0.49+9bf2742) answers fine:

```
REPO        PLAN                 RUN       STAGE  STATUS   PORT   PID   UP
* conductor Karvansara core...   9647f1b8  KS10   Running  4318   6716  5h14
```

One run on this machine: this one, PID 6716, the conductor that supervised this session. It will
have exited by the time the owner acts. Verify any other pid's command line with
`Get-CimInstance Win32_Process` before touching it (trap 3); never kill by name.

Worth knowing, because it looks like a contradiction: **`ps` works but `task --list` does not.**
The PATH engine refuses `task --list` with *"run.db schema version is newer (14) than supported
(13)"* — bug #45. `ps` reads the per-run discovery files, not the catalogue, so it is unaffected.
Step 1 is therefore safe on the old engine; steps that read the store are not (see step 7).

### 2. merge feat/karvansara → master — FAST-FORWARD, no conflict is possible

- `git rev-list --count feat/karvansara..master` = **0** — master is wholly contained in the branch.
- `git rev-list --count master..feat/karvansara` = **99**.
- master = origin/master = `304fc5b`; feat/karvansara = origin/feat/karvansara = `05a3a5b`. Both in
  sync with the remote, so nothing needs fetching first.

A conflict check was attempted with `git merge-tree --write-tree` and this git is too old for that
form (exit 129, prints the three-arg usage). It does not matter: with master an ancestor, the merge
is a fast-forward and conflicts cannot arise. `--no-ff` is the owner's call for history shape only.

### 3. rename `## [Unreleased]` — MANDATORY, and here is the proof it is mandatory

CHANGELOG.md today: `## [Unreleased]` at line 21, next heading `## [0.4.0] - 2026-08-05` at line
155. The section is intact (KS10.2 measured it: `sh tools/changelog-section.sh Unreleased` exits 0).

But the guard does not ask for `Unreleased`. `.github/workflows/release.yml:66,73` strips the `v`
from the tag and runs `tools/changelog-section.sh "<version>"`. Run today, that is:

```
$ sh tools/changelog-section.sh 0.4.1
exit=1
changelog-section: no section for 0.4.1 in CHANGELOG.md.
  Expected a heading '## [0.4.1] - <date>' with at least one line under it.
```

This is the contract's own named risk, confirmed: tag before the rename and the release dies in the
guard job before five platforms compile. Rename to `## [0.4.1] - <date>`, re-run
`sh tools/changelog-section.sh 0.4.1`, read what it prints — that text becomes the release body
verbatim — and commit that before tagging.

### 4. tag v0.4.1 — the version assertion is real, and it is not in the guard job

Correction to the contract's acceptance step 4, which places the version-vs-tag assertion in
`guard`. It is in **`build`**, on the linux-x64 leg only, `release.yml:157-163`:

```yaml
actual=$(./out/conductor version --short)
expected="${{ needs.guard.outputs.version }}"
case "$actual" in "$expected"|"$expected"+*) echo "version matches the tag." ;;
```

The guard job (`release.yml:52-80`) only resolves the tag, runs changelog-section.sh, and uploads
`release-body.md`. Both jobs matter; the failure surfaces one job later than the contract implies.

**Which tag.** Version comes from MinVer over tag height (`src/Conductor/Conductor.csproj:43`,
`fetch-depth: 0` at `release.yml:125`). Latest tag is **v0.4.0**; the current head already reports
`0.4.1-alpha.0.49+9bf2742`. Tagging **v0.4.1** therefore yields `0.4.1`, and the `$expected+*` arm
of that case statement accepts the `+sha` suffix.

### 5. reinstall via tools/install.ps1 — the first install of this run, and it also fixes bug #45

Post-install, `conductor version` must report `0.4.1` and match the releases page. This is also what
lifts the schema-14-vs-13 refusal, so the PATH shim can read this run's store again.

### 6. `gh auth refresh -s project` — optional, only if KS9.3 is to be revisited

Today's token carries `repo` but not `project`; KS9.3 is SKIPPED on exactly that ground.

### 7. `github sync --backfill` this run — MUST come after step 5, not before

Ordering constraint, not a contract error: the backfill reads the run store, and the pre-install
PATH engine cannot (bug #45, step 1). Run it from the reinstalled engine. This is the first
sanctioned write to a real repository in this era — trap 11 lifts here and nowhere earlier — and
the first time KS9's idempotence meets a repo that already has issues, so read what it creates.

### 8. merge the payesh PR — both checkouts verified clean, and the paths are both real

The contract's `files` names `C:/Code/conductor-site-harvest`; memory and the handoff name
`C:/code/conductor-site`. Both exist and both are shaahink/payesh — there is no stale path here:

- `C:/code/conductor-site` — `main` at `43b59e4`, clean. The live checkout; a push here deploys to
  payesh.vercel.app (trap 16).
- `C:/Code/conductor-site-harvest` — `ks01/harvest-dedup-refresh` at `e9077b7`, clean. The PR
  branch, PR #1, open.

Session #24's handoff flags the second commit of that PR as relaxing a privacy rule (a public
repo's run slug stops being secret). That one wants a real read, not a rubber stamp. This run
`9647f1b8` stays excluded from the corpus until `anonymise.json` gives it a label, scenario,
repoKey and disposition.

### 9. close the era

KS10.3 DONE in the tracker, brief to `docs/history/`, tracker to
`docs/history/archive/trackers/`, per the convention `docs/dev/README.md` states.

## What this session did not do, and why

Every one of the nine steps is an owner action by contract: merging to master, pushing a tag,
overwriting the engine that is driving this very session (trap 1), writing to a real GitHub repo
(trap 11), and merging to a live public site (trap 16). A session performing any of them would be
violating the checkpoint's first acceptance line. KS10.3 is escalated to the owner in the tracker's
handoff block, and the row is left TODO and unamended.
