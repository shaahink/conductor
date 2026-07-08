# B0 tooling drafts

These are **reference drafts** for stage B0. They are intentionally NOT active build files (they live
here as `.draft`/`.md`, not at the repo root) so authoring this plan does not change the current
build. The B0 session promotes them to their real locations (`.editorconfig`, `Directory.Build.props`,
`Directory.Packages.props`) and fixes every diagnostic they surface — **by fixing the code, never by
lowering a severity** (BATON-BRIEF §7, A17).

Contents:
- `editorconfig.draft` → repo-root `.editorconfig`
- `Directory.Build.props.draft` → repo-root `Directory.Build.props`
- `Directory.Packages.props.draft` → repo-root `Directory.Packages.props`
- `meziantou-ruleset.md` → rationale + the curated rule severities to fold into `.editorconfig`
  (also the source for ADR-0001).

B0 must re-measure the 56-test baseline after promotion; a green build under
`TreatWarningsAsErrors=true` on `net10.0` is the B0.2 gate.
