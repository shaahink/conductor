# DV7.2 — the payesh harvest, re-run at the era close

Session 22, 2026-08-26. The last open item of DV7.2: re-run the field guide's
harvest against Conductor's real run store, on a branch, with a PR, never
pushing that repo's main (trap 15).

**PR: https://github.com/shaahink/payesh/pull/3** — base `ks12/harvest-era-close`
(PR #2, still open), head `dv7/harvest-era-close`. `main` was never pushed:
`git push -u origin dv7/harvest-era-close` is the only push this session made to
that remote.

Repo: `C:/code/conductor-site` (shaahink/payesh). Commits `eac0f80`, `f0ff21b`.

## 1. The re-harvest itself — the machine still runs, and nothing moved

    C:/code/conductor-site> npm run harvest
    harvest: 18 runs published, 15 excluded by anonymise.json (aa916828, 59024318,
    f0f150ea, 9491891f, c5fe473d, 380c587c, 6cf402fe, 9647f1b8, fe33af65, 8faf849d,
    d6fd22ba, 105b2731, 0ebd34b6, 0d8d372e, f5066ddc) · 340 sessions · $3,016.29 ·
    648/677 gates green

    C:/code/conductor-site> git diff --stat
     src/data/corpus.json | 2 +-
    -  "generatedAtUtc": "2026-08-19T19:11:04.892Z",
    +  "generatedAtUtc": "2026-08-26T14:53:05.423Z",

The store has grown by fifteen runs since the KS12.2 harvest and the published
corpus did not move a digit. That is the fail-closed rule working: an era that
added new event types, a schema version and a new process leaked nothing.

Read-only throughout — `collect()` opens each store with `new DatabaseSync(path,
{ readOnly: true })` (scripts/harvest.mjs:421), and the live Divan run's store is
the same file (`...\runs\conductor-karvansara-core---the-open-door-308cfb9b\run.db`
carries aa916828, 9491891f, 9647f1b8 and e9e21d10). No write path was opened, so
trap 18 does not arise.

## 2. What the no-op exposed — the corpus stops in early August

The 15 excluded runs are not noise. `conductor history --json --limit 0`,
filtered to them:

| run | status | checkpoints | sessions | what it is |
|---|---|---|---|---|
| 9647f1b8 | Aborted | 30/32 | 24 | the release era (KS0..KS10, aborted at the ship stage) |
| 9491891f | needs_human | 23/24 | 23 | the era after it (KS11..KS12, parked at the owner's ship step) |
| aa916828 | running | 21/23 | 20 | **this run** |
| + 12 others | | | | product/client work, three scratch rigs, and the site's own run |

Two entries in `anonymise.json` (label, scenario, repoKey `orchestrator` — no
plan name, no repo path, no client anything) put the two closed eras in the
record. Every figure recomputed by the machine:

    C:/code/conductor-site> npm run harvest
    harvest: 20 runs published, 13 excluded by anonymise.json · 387 sessions ·
    $3,487.85 · 781/813 gates green

    18 runs -> 20 · 340 sessions -> 387 · $3,016.29 -> $3,487.85 · 648/677 -> 781/813

**Not published, deliberately:** the live Divan run. At 21/23 its figures would
freeze at a number that is wrong by the time anyone reads them; it gets its
entry from the harvest after its last checkpoint closes. The other eleven
exclusions are content calls that belong to the owner and are named in the PR.

## 3. The break naming them found, and the bar that now holds it

`needs_human` — the engine's word for a parked run — reached `disposition()`
(scripts/harvest.mjs:930) as an unknown status and would have been published as
a word RunTable.astro, RunCards.astro and EvidenceStrip.astro paint in no role.
Same class as the `closed` break KS12.2 found. Two changes:

- `needs_human` joins `NEEDS_DISPOSITION` — a park somebody answered and a park
  nobody returned to are the same row, so `anonymise.json` says which.
- the general form: `PUBLISHABLE = {completed, abandoned, paused, aborted}` is
  enforced **in the harvest**, where the corpus is made. Twice now a word the
  page cannot paint has been caught by `corpus.test.mjs` *after* the file was
  written; the store's vocabulary is the engine's and grows without asking.

Both proven red before the fix (`/tmp` transcript reproduced here):

    ### PROOF 1 - a parked run with no disposition is refused
    Error: anonymise.json: run 9491891f ("the-era-after-the-release") is marked
    needs_human in the store, so it needs a "disposition" - "abandoned" or "paused".
    ...  at resolveStatus (scripts/harvest.mjs:936)

    ### PROOF 2 - a disposition the page paints in no role is refused
    Error: run 9491891f ("the-era-after-the-release") resolves to status "parked",
    which this site paints in no role - it would render as unstyled text beside runs
    whose state the page does colour. ...  at disposition (scripts/harvest.mjs:923)

And pinned by two new tests in `test/harvest.test.mjs`, run red against the
unguarded harvest first:

    node --test test/harvest.test.mjs   (guard removed)
    ✖ a parked run has to say the same thing: the engine's word is not an outcome
    ✖ a status the site paints in no role is refused where it is made, not where it renders
    ℹ tests 27 · pass 25 · fail 2

    node --test test/harvest.test.mjs   (guard restored)
    ℹ tests 27 · pass 27 · fail 0

## 4. The site's own gates, after

    npm test            ℹ tests 128 · pass 128 · fail 0     (126 before, +2 new)
    npx astro check     0 errors, 0 warnings, 3 hints
    npm run build       39 pages in 3.76s; 545 annotations on 22 pages all resolve
    npm run evidence    corpus.json is current - 20 runs, 387 sessions, $3,487.85;
                        every key cited by 18 content entries resolves
    npm run anonymity   RED: 77 findings

The anonymity check is red and was red before this branch. 76 findings are the
ordinary-noun private repo name of bugs #47 and #41. The 77th is new to the
ledger and not caused here — a plan title's generic wording, revealed as "the
engine knows", matched inside `src/components/FigureQueue.astro`, a component
this branch does not touch and which the phrase is hard-coded into. Filed as
**bug #83**.

## 5. Owner, at DV7.3

Merging "the payesh PR" now means two: **#2 first (or as the base), then #3.**
#3 is stacked on #2 precisely because #2 is unmerged; merging #3 into `main`
alone would drop KS12.2's hand-closed-run fix.
