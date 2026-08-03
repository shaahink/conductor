# S1.3 + S1.4 — Shamshir `Release` is green end to end

Session #11, 2026-08-03. Evidence for checkpoints S1.3 and S1.4.

## What was still red when this session started

The `fleet-green` gate reported exactly one failure:

```
* shaahink/Shamshir / Release : latest run on main concluded 'failure' (run 30765474447)
```

and the fix branch's own `Release` run 30832399158 had failed too — so the branch was
not mergeable on green either.

## The failure, read from the run rather than guessed

`gh run view 30832399158 --repo shaahink/Shamshir --log-failed` showed the run getting
all the way to step 11 and dying there:

```
error NETSDK1152: Found multiple publish output files with the same relative path:
  src\TradingEngine.Host\appsettings.Development.json,
  src\TradingEngine.Web\appsettings.Development.json,
  src\TradingEngine.Host\appsettings.json,
  src\TradingEngine.Web\appsettings.json
  [src\TradingEngine.Web\TradingEngine.Web.csproj]
```

This is the third never-exercised step the run has uncovered in a row. Before the Node
fix the build died at `MSB3073` and nothing after it had ever run; now `dotnet build`,
`dotnet test` and `dotnet publish src/TradingEngine.Host` all pass and the *next* unrun
step is the one that fails.

Root cause: `TradingEngine.Web` references `TradingEngine.Host` for the engine types it
drives. `Host` is an executable worker carrying its own `appsettings*.json`, and those
ride the project reference into `Web`'s publish set at exactly the relative paths
`Web`'s own configuration occupies. This was never only a build break — before the SDK
started refusing to guess, whichever copy was written last silently became the web
app's configuration.

## The fix (Shamshir `017c87c`)

A target in `src/TradingEngine.Web/TradingEngine.Web.csproj` removes the Host-owned
`appsettings*` entries from `ResolvedFileToPublish`, between `ComputeFilesToPublish`
and `_HandleFileConflictsForPublish`.

Deliberately **not** `ErrorOnDuplicatePublishOutputFiles=false`: that silences the
diagnostic and leaves an arbitrary copy winning, which is the exact latent bug. The web
app ships its own config; the worker's belongs in the worker's own publish, which
`release.yml` still produces separately at step 10.

Local proof before pushing:

```
dotnet publish src/TradingEngine.Web -c Release -r win-x64 --self-contained -o publish/web
  -> TradingEngine.Web -> C:\Code\Shamshir\publish\web\
Get-FileHash publish/web/appsettings.json == Get-FileHash src/TradingEngine.Web/appsettings.json
  -> MATCHES
```

`publish/web/` contains only `appsettings.json` and `appsettings.Development.json` —
Host's `appsettings.Backtest.json` is gone, as intended.

## Remote proof — `Release` run 30833477182, fix branch, every step green

```
1  success  Set up job
2  success  Run actions/checkout@v7
3  success  Run actions/setup-node@v7
4  success  Run npm ci --legacy-peer-deps
5  success  Run npm run build
6  success  Run actions/setup-dotnet@v6
7  success  Run dotnet restore
8  success  Run dotnet build --no-restore -c Release
9  success  Run dotnet test --no-build -c Release --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
10 success  Run dotnet publish src/TradingEngine.Host -c Release -r win-x64 --self-contained -o publish/engine
11 success  Run dotnet publish src/TradingEngine.Web -c Release -r win-x64 --self-contained -o publish/web
12 success  Run softprops/action-gh-release@v3
22 success  Post Run actions/setup-dotnet@v6
23 success  Post Run actions/setup-node@v7
24 success  Post Run actions/checkout@v7
25 success  Complete job
```

https://github.com/shaahink/Shamshir/actions/runs/30833477182

That is the first green `Release` this repo has had; there were 12 consecutive failures
from 2026-07-16 onward before it.

### S1.3 specifically — the replacement release action is exercised, not assumed

Step 12 is `softprops/action-gh-release@v3`, replacing the archived
`actions/create-release@v1`. It did not merely exit 0 — it produced an artefact:

```
gh release list --repo shaahink/Shamshir
Release v22   Pre-release   v22   2026-08-03T16:52:53Z
```

Marked pre-release because `github.ref_name != 'main'`, exactly as `release.yml`
specifies for a release cut off a branch.

## The lint check, and why it was fixed rather than merged past

`pr.yml`'s `lint` job (`dotnet format --verify-no-changes`) had existed for a while but
had **never run once** — `pr.yml` only triggered on pull requests into `develop`, and
every pull request the repo has actually had went into `main`. `gh run list --workflow
"PR Build & Test"` returns exactly one run before this session: the one session #10
caused by adding `main` to the trigger.

Its first run failed on ~150 `CHARSET` errors across `tests/` plus two `IDE0011` brace
errors in `src/TradingEngine.Adapters.CTrader/`. None of it introduced by this branch —
it is drift the gate was never in a position to catch.

Fixed with plain `dotnet format` (Shamshir `8567898`), so the repo matches the
`.editorconfig` it already declares (`charset = utf-8-bom`,
`csharp_prefer_braces = when_multiline`). 853 files, mechanical: a UTF-8 BOM where one
was missing, braces on multi-line `if`/`lock` bodies, object initialisers one member per
line. The gate was not narrowed and the `lint` job was not touched.

`build-and-test` re-ran on the reformatted tree and passed (5m37s), so the reformat
changed no behaviour.

## Merge and the default branch

RESULT_PLACEHOLDER
