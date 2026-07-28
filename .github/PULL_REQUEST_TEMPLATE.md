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
