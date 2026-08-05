<#
.SYNOPSIS
  Read run.db from a PowerShell rig, over the MCP `run_query` tool.

.DESCRIPTION
  SF1.2 deleted `conductor report --query`. It was the CLI half of the SQL console the owner asked to
  delete ("delete this stupid sql query report and its traces"), and the W3/W5 rigs were its only
  non-human callers -- they used it to read assertions back out of a finished run's run.db.

  Ad-hoc SQL did NOT die with it. It survives where it is actually asked for: the MCP `run_query`
  tool, which is what `conductor chat` uses. This helper drives that tool directly over stdio, so the
  rigs keep asserting against the database AND exercise the surviving path while they do it.

  Deliberately out-of-process against the shipped binary, matching the rigs' own rule (W2.1's lesson:
  a harness we wrote ourselves is too lenient to be evidence). Nothing here calls into engine classes.

  ASCII only -- Windows PowerShell 5.1 (repo trap 7).

.PARAMETER Exe
  Path to conductor.exe. The rigs already resolve this.

.PARAMETER StateDir
  The plan's state directory -- a scratch repo's `.conductor`, which holds events.jsonl. Since K3.1
  it does NOT hold run.db; see -RunDb.

.PARAMETER RunDb
  The database file. Defaults to $env:CONDUCTOR_RUN_DB (what the rigs set for the engine they spawn),
  then to the pre-K3.1 `<StateDir>/run.db`.

.PARAMETER Sql
  A SELECT statement. run_query rejects anything else, which is the point.

.OUTPUTS
  One string, shaped like the old `report --query` table output: a header line of column names, then
  one line per row with values separated by ' | '. "no rows" when the query matched nothing, and
  "query failed: <reason>" when the tool refused. The rigs assert with -match against this text, so
  the shape matters more than the formatting.

.EXAMPLE
  . "$PSScriptRoot\..\lib\run-query.ps1"
  Invoke-ConductorQuery -Exe $Exe -StateDir (Join-Path $scratch ".conductor") `
      -Sql "SELECT type FROM events ORDER BY seq DESC LIMIT 6"
#>

function Invoke-ConductorQuery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Exe,
        [Parameter(Mandatory = $true)][string] $StateDir,
        [Parameter(Mandatory = $true)][string] $Sql,
        [string] $RunDb
    )

    # K3.1 took run.db OUT of <repo>/.conductor and into a machine-level home keyed by (repo, plan),
    # and this helper was not told. It kept asserting Test-Path "<StateDir>/run.db" and kept letting
    # mcp-serve fall back to its pre-K3.1 default (run.db beside --events), so every rig that reads a
    # finished run back -- w5/rehearsal, w3/window-close, sf1-2 -- answered "query failed: no run.db"
    # on a perfectly healthy engine. No CI job runs these rigs, so nothing caught it for a whole era.
    #
    # Resolution order mirrors StateHome.Resolve's first rule and stops there deliberately: a rig
    # NAMES the database it wants (its own scratch copy), so deriving a slug here would be guessing at
    # something the caller already knows. -RunDb wins; then CONDUCTOR_RUN_DB, which is what the rigs
    # set for the engine they spawn; then the pre-K3.1 layout, for a caller pointed at an old tree.
    if (-not $RunDb) { $RunDb = $env:CONDUCTOR_RUN_DB }
    if (-not $RunDb) { $RunDb = Join-Path $StateDir "run.db" }
    if (-not (Test-Path $RunDb)) { return "query failed: no run.db at $RunDb" }

    # --events must still point inside StateDir (the event log did NOT move), but run.db is passed
    # explicitly now rather than derived from the events directory.
    $events = Join-Path $StateDir "events.jsonl"
    $journal = Join-Path ([System.IO.Path]::GetTempPath()) ("cq-" + [Guid]::NewGuid().ToString("N") + ".jsonl")

    # The server answers a bare tools/call -- no initialize handshake -- one JSON line in, one out.
    $req = @{
        jsonrpc = "2.0"; id = 1; method = "tools/call"
        params  = @{ name = "run_query"; arguments = @{ sql = $Sql } }
    } | ConvertTo-Json -Depth 6 -Compress

    # The request goes in as a FILE redirected by cmd, not through Process.StandardInput. Writing the
    # line through a redirected StandardInput handle under Windows PowerShell 5.1 hands the server
    # bytes it answers with {"code":-32700,"message":"Parse error"} -- the stdin writer's encoding is
    # not the UTF-8 the reader expects, and nothing about the failure says so. A redirected file is
    # ASCII on the wire and works first time. Verified against the fresh build during SF1.2.
    $reqFile = Join-Path ([System.IO.Path]::GetTempPath()) ("cq-req-" + [Guid]::NewGuid().ToString("N") + ".json")
    Set-Content -Path $reqFile -Value $req -Encoding ascii

    try {
        $out = cmd /c "`"$Exe`" mcp-serve --events `"$events`" --journal `"$journal`" --run-db `"$RunDb`" < `"$reqFile`"" 2>$null
        $out = ($out | Out-String)
    }
    finally {
        Remove-Item $journal -ErrorAction SilentlyContinue
        Remove-Item $reqFile -ErrorAction SilentlyContinue
    }

    # Take the first line that parses as a JSON-RPC reply; the server may log banner lines around it.
    $reply = $null
    foreach ($line in ($out -split "`r?`n")) {
        if ($line.Trim() -eq "") { continue }
        try { $candidate = $line | ConvertFrom-Json } catch { continue }
        if ($candidate.PSObject.Properties.Name -contains "result") { $reply = $candidate; break }
    }
    if ($null -eq $reply) { return "query failed: no JSON-RPC reply from mcp-serve" }

    # tools/call wraps the tool's own JSON payload in result.content[0].text.
    $payloadText = $null
    if ($reply.result.PSObject.Properties.Name -contains "content" -and $reply.result.content.Count -gt 0) {
        $payloadText = $reply.result.content[0].text
    }
    if (-not $payloadText) { return "query failed: reply carried no tool payload" }

    $payload = $payloadText | ConvertFrom-Json
    if (-not $payload.ok) { return ("query failed: " + $payload.error) }
    if ($null -eq $payload.rows -or @($payload.rows).Count -eq 0) { return "no rows" }

    $rows = @($payload.rows)
    $columns = @($rows[0].PSObject.Properties.Name)
    $lines = @(($columns -join " | "))
    foreach ($r in $rows) {
        $vals = foreach ($c in $columns) { [string]$r.$c }
        $lines += ($vals -join " | ")
    }
    return ($lines -join "`n")
}
