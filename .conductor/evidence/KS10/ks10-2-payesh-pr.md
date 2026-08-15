# KS10.2 — the field guide, re-harvested against the deduped catalogue

Session #24, 2026-08-15. Worktree `C:/Code/conductor-site-harvest`, branch
`ks01/harvest-dedup-refresh`, base `6e5f395`.

**PR: https://github.com/shaahink/payesh/pull/1** — `state: OPEN`, `baseRefName: main`,
`headRefName: ks01/harvest-dedup-refresh`, `mergeable: MERGEABLE`.
Fetched as raw JSON via `gh pr view 1 --json url,state,baseRefName,headRefName,title,mergeable`
piped through `ConvertFrom-Json` — never `gh --jq` from PowerShell (trap 13).

**`main` was not touched.** `git rev-parse origin/main` → `43b59e42019ffb3821d25169e4739899e4a05112`,
the same commit it was on before this session. The owner merges at KS10.3; a session does not deploy
a live public site (trap 16).

## The engine used

The harvest shells `conductor history --json --limit 0`, `conductor money` and `conductor budget`,
which resolve the PATH shim. The published shim is `0.4.0` and can no longer open the store its own
successor migrated, so the working-tree build went first on `PATH`:

```
=== which conductor ===
C:\code\conductor\src\Conductor\bin\Debug\net10.0\conductor.exe
0.4.1-alpha.0.101+263c7f8a5d1c.dirty
```

`tools/install.ps1` was never run (trap 1).

## What the proof found

The point of this checkpoint's payesh half is to prove KS0.x did not break the store's most public
consumer. It had — three ways, all present on the branch before this session touched it.

### 1. `npm run anonymity` — 69 findings on 22 pages, and could not be made to pass

Every finding was one literal: a private repository on this machine is a directory called `website`,
and the whole-name check kept every private repo basename without asking whether the site is already
allowed to say the word. `404.html` was reported for using it in prose.

`keepTokens()` has always asked that question — `GENERIC` carries `site`, `code`, `docs`, `repo`,
`build` — which is why a **multi-word** repo name never poisoned its component words. The whole-name
check at `scripts/anonymity.mjs:252` consulted only `publicNames`. The only route to green was
deleting an English word from the site's prose, and a rule whose remedy is "say less" stops being run.

Nothing was given up: `keep(paths, run.repo, …, 8)` still forbids `C:\code\website`. Only the bare
noun, which identifies nobody, is exempt. Filed as conductor bug **#47** before any edit.

### 2. The site could not quote its own subject

The one remaining finding: `the engine knows`, on `concepts/human-in-the-loop`. It comes from the
karvan run's real slug —

```
runId : df9c4af8044442cca197463d9af7e670
slug  : conductor-karvan-core---the-engine-knows-what-it-did-and-what-it-cost-b4640aef
repo  : C:/code/conductor
```

— a run in a repository the site is explicitly allowed to name. `payesh` already has a test called
*"a public repository is never listed as a private name"*, and that exemption covered one field of
three: the repo name, not the run's slug and not the three-word phrases inside it. So a field guide
whose entire subject is that engine could not write a sentence that is in that engine's own README,
CHANGELOG and release notes.

Scoped to `PUBLIC_NAMES` on purpose. A private repository's slug and phrases stay secret in all three
fields, and the `harrowgate-linens` fixtures still prove it. **This is the change flagged in the PR
body as the one for the owner to look hardest at** — it relaxes a privacy rule, and it belongs to the
owner to accept.

### 3. `npm test` was red on the shipped corpus

```
✖ no row in the shipped corpus has an unlabelled status
  the-first-false-start has status "closed", which RunTable.astro paints in no role — it would
  render as unstyled text beside runs whose state the page does colour
```

This is KS0.2 reaching the site. `disposition()` resolved `running` against `anonymise.json` because
the store cannot tell an abandoned run from a paused one. Then `conductor run close` shipped — an
operator-written terminal status for a record whose engine never got to close it — and the four
phantom rows stopped saying `running` and started saying `closed`. `closed` carries "somebody closed
this record", not "this is how the work ended": the same hole, one word later. `closed` now resolves
through the disposition exactly as `running` does, and `corpus.json` is regenerated.

The branch's own base commit message (`6e5f395`) reads *"…and the false starts say closed"*, so this
shipped knowingly and the site's test is what caught the consequence.

## Green after (all four, in this order)

```
=== npm run harvest ===
harvest: 18 runs published, 8 excluded by anonymise.json (9647f1b8, fe33af65, 8faf849d, d6fd22ba,
105b2731, 0ebd34b6, 0d8d372e, f5066ddc) · 340 sessions · $3,016.29 · 648/677 gates green
=== harvest exit: 0 ===

=== npm test ===
ℹ tests 128   ℹ pass 128   ℹ fail 0
=== test exit: 0 ===

=== npm run evidence ===
evidence: corpus.json is current — 18 runs, 340 sessions, $3,016.29; and every key cited by 18
content entries resolves.
=== evidence exit: 0 ===

=== npm run build ===
[@shaahink/sitekit:check-annotations] 543 annotations on 22 pages all resolve
[build] 39 page(s) built
=== build exit: 0 ===

=== npm run anonymity ===
anonymity: 70 built files carry none of 77 machine paths, 21 private names, 8 distinctive tokens,
52 three-word run-name phrases or 8 secret shapes — of those, 27 vendored file(s) were checked for
paths and secrets only, not for names; NOT CHECKED for quoted prose: no docs/dev/FIELD-NOTES-*.md
on this machine, which is expected in CI because they are untracked.
=== anonymity exit: 0 ===
```

`npm run anonymity` needs `npm run build` first by design: it reads `dist/`, the bytes a stranger
would download, not the source they came from.

## Left for the owner at KS10.3

This era's own run, `9647f1b8`, is in the excluded list. The harvest **fails closed**: a run with no
`anonymise.json` entry is left out of the corpus entirely rather than published under its real name.
If the karvansara run should appear on the field guide, it needs a `label`, `scenario` and `repoKey`
there — plus a `disposition`, because the store will still call it non-terminal until the run ends.
No figure was typed in; every number above came back from the harvest.
