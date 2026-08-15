<!--
Thanks for this. Conductor's review IS the gate battery — see CONTRIBUTING.md. The checklist below
is short on purpose; it asks for the things a reviewer cannot check for you.
-->

## What this changes

<!-- What it does, and what would have caught it going wrong. -->

## Evidence

<!--
This project's thesis is that a claim is worth nothing without an independent check. A PR that says
"fixed" and a PR that adds the test which fails before and passes after are not the same PR.

Paste what you ran. If your change touches the wire — the MCP server, the control plane, the prompt
an agent actually receives — prefer a test that goes through the real surface: an in-process harness
we wrote ourselves has twice been too lenient to catch a real defect.
-->

## Gate battery

Run all four, in order (see CONTRIBUTING.md). On Linux/macOS you can run the first three and let CI's
`windows-latest` leg run the ratchet.

- [ ] `dotnet build Conductor.slnx`
- [ ] `dotnet test Conductor.slnx`
- [ ] `cd face-go; go build ./...; go vet ./...; go test ./...`
- [ ] `powershell -File tools/gates/ratchet.ps1` — exact path; a wrong path exits 0 and proves nothing
- [ ] `conductor demo` still completes (cross-platform, no credentials)

## The ratchet

The ratchet fails if the **bar** moved down, not if the code is wrong. If you needed to raise a
ceiling, delete a test, or change a gate command, say why here — that is a human decision, not a
diff.

- [ ] No test deleted, no analyzer suppression added, no gate command softened
- [ ] If an ADR settled what I'm changing, I amended the ADR in this PR

## If you added or renamed a verb

A verb lives in **four** places and the docs tests fail the build when they disagree — this is a
checklist, not a request for extra work.

- [ ] `src/Conductor/Program.cs` (registered, and `.IsHidden()` only if it is genuinely not a verb
      to reach for — hiding one to make `K7_2DocsVerbCoverageTests` green is the cheat that test exists to stop)
- [ ] `docs/cli.md` — a reference row
- [ ] `docs/operating.md` §2 "Full command reference" — the page an agent driving conductor is pointed at
- [ ] `src/Conductor/Commands/CompletionCommand.cs` — the one `Verbs` constant both shells read
- [ ] Quoted it in `README.md`? Then every flag on that line must be one the command's settings type
      declares — strict parsing means a stale flag exits non-zero for whoever copies it
