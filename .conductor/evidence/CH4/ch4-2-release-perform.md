# CH4.2 — `conductor release perform`: what is mechanical is performed, what is judgement is named

**Measured 2026-08-27, session 5 of Charkh, through the FRESH BUILD**, end to end against a
**scratch git repository under the temp directory** (`C:/Users/shahi/AppData/Local/Temp/ch42-rig`)
with its own tiny plan — never this repository.

The failure this is against, stated once: `ks12-3-owner-runbook.md` listed **seven** acts and six of
them were never carried out. That is not carelessness. In prose, *"this one is yours to do"* and
*"nobody did this one"* are the same sentence. The next era's runbook opened by discovering it.

---

## The shape

`ReleaseAct` carries **two** independent fields, and that is the whole idea:

| field | question | changes? |
| --- | --- | --- |
| `Kind` | whose act is this, **ever**? `mechanical` \| `owner` | never |
| `State` | what became of it **this time**? `ready` \| `done` \| `nothing` \| `refused` \| `stopped` \| `failed` | every run |

An `owner` act is `stopped` on **every run, whatever the state** — it is never `nothing`, because
"nothing to do" is precisely what six unperformed acts looked like.

| act | kind | what decides it |
| --- | --- | --- |
| `changelog` | mechanical | rename `## [Unreleased]` → `## [<v>] - <date>`; **refused over a placeholder body** |
| `merge` | mechanical | gated on **CH4.1's own** merge verdict — never a second opinion |
| `tag` | mechanical | annotated `v<v>`; **refused until the section it will publish exists**; never moves an existing tag |
| `docmove` | mechanical | `git mv` **plus** `tracker` / `planDoc` / `readOrder` repointed, in ONE commit |
| `version` | owner | the number itself. MinVer derives a build id, not a release name |
| `split` | owner | one release or two is a call about what the world reads |
| `corpus` | owner | whether a run joins the published corpus — with the run ids and the once-only rule (#79) |
| `reinstall` | owner | `tools/install.ps1` overwrites the binary every run on this machine executes |
| `publish` | owner | `git push origin <base>` and `git push origin v<v>` — the tag push starts the release build |

**Exit codes.** `0` nothing left to do · `1` an act refused or failed (and nothing after it was
attempted) · **`2` the mechanical acts are done and the owner's are not — which is what a *finished*
run looks like.** A script that read `0` as "the era is closed" would be reading it off a document
again.

**Dry run is the default.** `--yes` performs. Everything is local: nothing is pushed, no binary is
swapped, no issue is created.

---

## The hard refusal — proved against this repository

```
$ dotnet run --project src/Conductor -- release perform -p plans/charkh/core.plan.json --tag 0.6.0 --yes
refusing: a conductor run is live in C:/code/conductor\.conductor (engine pid 5248).
this verb rewrites the CHANGELOG, moves the plan's tracker and repoints the plan itself —
doing that under a live session pulls the ground out from under it. Let the run end first.
exit=1
```

It stops **before measuring anything**. Deliberately the PLAN's engine lock and not the machine's: a
conductor running in another repository is a reason not to swap the binary (an owner act) — it is not
a reason this repository cannot be merged. Capture:
[`ch4-2-rig-idempotence-and-refusal.txt`](ch4-2-rig-idempotence-and-refusal.txt)

---

## Live run 1 — the rehearsal, exit **2**

[`ch4-2-rig-dryrun.txt`](ch4-2-rig-dryrun.txt). All four mechanical acts `→ ready`, five owner acts
`? stopped`, nothing written.

## Live run 2 — `--yes`, exit **2**

[`ch4-2-rig-perform.txt`](ch4-2-rig-perform.txt)

```
✓ changelog  mechanical renamed '## [Unreleased]' to '## [0.1.0] - 2026-08-27' and committed it
✓ merge      mechanical fast-forwarded master to feat/aban (a5f5aadfa136)
✓ tag        mechanical created annotated tag v0.1.0 at a5f5aadfa136 (local)
✓ docmove    mechanical moved 2 file(s) into the record and repointed the plan, in one commit
             docs/dev/ABAN-PLAN-2026-08-27.md -> docs/history/ABAN-PLAN-2026-08-27.md  (plan repointed)
             plans/aban/TRACKER.md -> docs/history/archive/trackers/ABAN-TRACKER.md    (plan repointed)
? version    owner      the release is named 0.1.0 because you said so
? split      owner      one release or two is a call about what the world reads, not about the tree
? corpus     owner      no run is owed a GitHub record
? reinstall  owner      the reinstall cannot happen yet - a conductor is live on this machine
? publish    owner      pushing is what makes any of this public
```

### Verified against the repository, not against the verb's own claims

[`ch4-2-rig-git-state.txt`](ch4-2-rig-git-state.txt) — read out of git afterwards:

```
298ad13 docs(release): the era's plan and tracker join the record
a5f5aad chore(release): CHANGELOG section for 0.1.0     <- v0.1.0 points here
...
v0.1.0 -> a5f5aadfa136…      master -> 298ad13afa0d…      (the tag is the released tree; the
                                                            doc move lands after it, as DV7.3 says)

$ sh tools/changelog-section.sh 0.1.0      exit=0, and it prints the era's real entries

"tracker":   "docs/history/archive/trackers/ABAN-TRACKER.md"
"planDoc":   "docs/history/ABAN-PLAN-2026-08-27.md"
"readOrder": [ "docs/history/ABAN-PLAN-2026-08-27.md", "docs/history/archive/trackers/ABAN-TRACKER.md" ]
"// a comment header the plan editor would drop": "kept on purpose"      <- still there

git status --porcelain      (empty)
```

The plan is edited by **targeted string replacement**, not through `PlanDocumentEditor`: that editor
rewrites the whole document from the model, dropping the comment header and normalising fields nobody
asked it to touch. An era-close commit that silently rewrote the plan would be indefensible in review,
so the rig's plan carries a comment header specifically to prove it survives.

## Live run 3 — asked again, exit **2**, nothing done twice

```
= changelog  mechanical CHANGELOG already has a [0.1.0] section
= merge      mechanical master already contains every commit of feat/aban
= tag        mechanical v0.1.0 already exists
= docmove    mechanical nothing left to move - the plan already points into the record
```

`git log -1` unchanged, `git status --porcelain` empty, `git tag -l` still one tag. Every mechanical
act survives being asked twice, because the owner will ask twice.

---

## Three defects the rig found, all fixed before this landed

Each was found by *running* the thing, and none would have been found by reading it.

1. **The dry run reported STOPPED for a sequence that would have succeeded.** A real run re-plans
   between acts, so by the time `tag` is decided the CHANGELOG section `changelog` just wrote is on
   disk. A dry run performs nothing — so every act was decided against the state *before* the
   sequence, and `tag` refused "no CHANGELOG section for 0.1.0 yet". A rehearsal that reports STOPPED
   for a run that would work is worse than no rehearsal: it teaches the owner to ignore it. The dry
   run now **projects** the one ordering dependency the sequence has, and says it is doing so.

2. **A merge already contained in the base reported as `refused`, not `nothing`.** On the second run
   `master` was one ahead of the branch — the doc-move commit this same verb had just landed on it —
   so the preflight was correctly red and the act refused. But the branch carries nothing the base
   lacks: the merge is the no-op git itself calls *"Already up to date"*. **"Already done" and
   "refused" are the two answers KS12.3 could not tell apart**, so containment is now asked *before*
   the preflight gate. Pinned by
   `A_branch_the_base_already_contains_is_nothing_to_do_even_when_the_base_has_moved_on`.

3. **A completed doc move reported as a collision, in a sentence that made no sense.** The probe
   derives the source from the PLAN, which the first run repointed — so on the second run `From` and
   `To` are the same path, the file is "there", and the destination is "occupied" by itself. The
   output read `docs/history/ABAN-PLAN-2026-08-27.md exists and docs/history/ABAN-PLAN-2026-08-27.md
   is not it`. `DocMove.AlreadyInPlace` now separates the two, and a genuine collision is still
   refused by name.

---

## Tests — 15, and each one is about a refusal or a distinction

That an act performs when its preconditions hold is the easy half, and the rig proves it end to end.
What a test pins, and a rig run cannot, is that **each precondition refuses BY NAME rather than the
act silently doing nothing**:

- `An_owner_act_is_stopped_at_on_every_run_and_is_never_reported_as_nothing_to_do` — the checkpoint,
  as one assertion, over two opposite fact sets.
- `A_complete_mechanical_sequence_still_exits_non_zero_because_the_owner_has_not_acted`.
- `The_changelog_rename_refuses_over_a_placeholder_body` — bug #88's shape, and the state this
  repository is in right now.
- `The_tag_refuses_until_the_section_it_will_publish_exists` and
  `An_existing_tag_is_nothing_to_do_and_is_never_moved`.
- `The_merge_act_refuses_on_the_preflights_verdict_and_quotes_it` — no second opinion.
- `The_doc_move_refuses_when_the_plan_cannot_be_repointed_in_the_same_act` — trap 13, as a behaviour.
- `A_move_the_plan_already_points_at_is_nothing_to_do_not_a_collision` and
  `A_destination_holding_a_different_file_is_still_refused_by_name` — the pair.
- `The_mechanical_acts_are_ordered_by_what_each_one_needs_from_the_last` — a reordering here is a
  released binary whose release notes do not exist.

Suite capture: [`ch4-2-tests.txt`](ch4-2-tests.txt).

---

## What CH4.2 deliberately does not do

- **It does not push.** Not the branch, not the tag. `publish` is an owner act with both commands,
  and the tag push is called out as what starts the release build across five platforms.
- **It does not reinstall.** `tools/install.ps1` overwrites the binary every run on this machine
  executes; that is named, with the instruction to re-check the process table at the moment of typing.
- **It does not back-fill a board.** Whether a run joins the published corpus is a decision about
  what the world sees.
- **It does not write the CHANGELOG's content.** It renames a heading over content somebody wrote,
  and refuses when nobody has.
