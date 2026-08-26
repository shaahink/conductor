# DV6.2 - the columns, driven LIVE through the freshly built engine.
#
# What this proves that a unit test cannot: the CLI's own wiring. `github sync --project N` resolves
# the board, passes the scope gate, builds a GithubProjectSync and reconciles COLUMNS - and does it
# through the real conductor binary, over real HTTP, with the real GraphQL documents on the wire.
#
# Why it is a loopback stub and not github.com: the machine's token does not carry the classic
# `project` scope (measured 2026-08-26: delete_repo, gist, read:org, repo, user, workflow), so no
# live board can be written without an interactive owner act - `gh auth refresh -s project`. The
# stub answers /user with the scope PRESENT, which is the only way to exercise the branch behind
# the gate at all. Everything downstream of that header is the engine's real code path.
#
# Safety: nothing here can reach github.com. CONDUCTOR_GITHUB_API is repointed at 127.0.0.1, the
# plan is a scratch plan in TEMP, and the run store is a COPY.
#
# Usage:  pwsh -File tools/dv6/dv6-2-live-proof.ps1

$ErrorActionPreference = 'Stop'
$port = 8791
$base = "http://127.0.0.1:$port"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$server = {
    param($port)

    $listener = New-Object System.Net.HttpListener
    $listener.Prefixes.Add("http://127.0.0.1:$port/")
    $listener.Start()

    $issues = @{}          # number -> hashtable
    $milestones = @{}      # title  -> number
    $items = @{}           # itemId -> hashtable(issue, option)
    $nextIssue = 100
    $nextItem = 1
    $documents = New-Object System.Collections.ArrayList
    $mutations = 0

    $options = @(
        @{ id = 'opt_todo'; name = 'Todo' },
        @{ id = 'opt_doing'; name = 'In Progress' },
        @{ id = 'opt_done'; name = 'Done' }
    )

    function Write-Json($ctx, $json, $scopes) {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $ctx.Response.StatusCode = 200
        $ctx.Response.ContentType = 'application/json'
        if ($scopes) { $ctx.Response.Headers.Add('X-OAuth-Scopes', $scopes) }
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.OutputStream.Close()
    }

    function Serialize-Issue($i) {
        $labels = ($i.labels | ForEach-Object { '{"name":' + (ConvertTo-Json $_ -Compress) + '}' }) -join ','
        return '{"number":' + $i.number + ',"node_id":"I_kw' + $i.number + '","title":' +
            (ConvertTo-Json $i.title -Compress) + ',"body":' + (ConvertTo-Json $i.body -Compress) +
            ',"state":"' + $i.state + '","html_url":"' + "http://127.0.0.1:$port/i/" + $i.number +
            '","labels":[' + $labels + '],"milestone":' +
            $(if ($null -eq $i.milestone) { 'null' } else { '{"number":' + $i.milestone + ',"title":"","state":"open"}' }) + '}'
    }

    while ($true) {
        $ctx = $listener.GetContext()
        $path = $ctx.Request.Url.AbsolutePath
        $query = $ctx.Request.Url.Query
        $body = ''
        if ($ctx.Request.HasEntityBody) {
            $reader = New-Object System.IO.StreamReader($ctx.Request.InputStream)
            $body = $reader.ReadToEnd()
            $reader.Close()
        }
        $method = $ctx.Request.HttpMethod

        if ($path -eq '/__mutations') {
            Write-Json $ctx ('{"mutations":' + $mutations + ',"items":' + $items.Count + '}') $null
            continue
        }

        if ($path -eq '/__state' -or $path -eq '/__shutdown') {
            $state = @{
                issues    = $issues.Values | ForEach-Object { @{ number = $_.number; title = $_.title; state = $_.state } }
                items     = $items.Values | ForEach-Object {
                                $opt = $_.option
                                $name = ($options | Where-Object { $_.id -eq $opt } | Select-Object -First 1).name
                                @{ issue = $_.issue; column = $name } }
                mutations = $mutations
                documents = $documents
            }
            Write-Json $ctx (ConvertTo-Json $state -Depth 6 -Compress) $null
            if ($path -eq '/__shutdown') { break }
            continue
        }

        if ($path -eq '/user') {
            # The whole point of the stub: the scope the real token lacks, present.
            Write-Json $ctx '{"login":"owner"}' 'repo, project'
            continue
        }

        if ($path -eq '/graphql') {
            $envelope = ConvertFrom-Json $body
            $doc = $envelope.query
            $vars = $envelope.variables
            [void]$documents.Add($doc.Substring(0, [Math]::Min(60, $doc.Length)))

            if ($doc -like 'mutation*') { $mutations++ }

            if ($doc -like '*repositoryOwner*') {
                $opts = ($options | ForEach-Object { '{"id":"' + $_.id + '","name":"' + $_.name + '"}' }) -join ','
                Write-Json $ctx ('{"data":{"repositoryOwner":{"projectV2":{"id":"PVT_rig","title":"DV6.2 rig board",' +
                    '"url":"http://127.0.0.1:' + $port + '/users/owner/projects/7","field":{"id":"PVTSSF_status","name":"Status","options":[' +
                    $opts + ']}}}}}') $null
                continue
            }
            if ($doc -like '*addProjectV2ItemById*') {
                $number = [int]($vars.content -replace '^I_kw', '')
                $existing = $items.Values | Where-Object { $_.issue -eq $number } | Select-Object -First 1
                if (-not $existing) {
                    $id = 'PVTI_' + $nextItem
                    $nextItem++
                    $items[$id] = @{ itemId = $id; issue = $number; option = $null }
                    $existing = $items[$id]
                }
                Write-Json $ctx ('{"data":{"addProjectV2ItemById":{"item":{"id":"' + $existing.itemId + '"}}}}') $null
                continue
            }
            if ($doc -like '*updateProjectV2ItemFieldValue*') {
                if ($items.ContainsKey($vars.item)) { $items[$vars.item].option = $vars.option }
                Write-Json $ctx ('{"data":{"updateProjectV2ItemFieldValue":{"projectV2Item":{"id":"' + $vars.item + '"}}}}') $null
                continue
            }
            if ($doc -like '*items(first:100*') {
                $nodes = ($items.Values | ForEach-Object {
                    $fv = if ($null -eq $_.option) { 'null' } else { '{"optionId":"' + $_.option + '"}' }
                    '{"id":"' + $_.itemId + '","content":{"number":' + $_.issue + '},"fieldValueByName":' + $fv + '}'
                }) -join ','
                Write-Json $ctx ('{"data":{"node":{"items":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[' +
                    $nodes + ']}}}}') $null
                continue
            }
            Write-Json $ctx '{"errors":[{"message":"the rig was sent a document it does not know"}]}' $null
            continue
        }

        $segments = $path.Trim('/').Split('/')
        # @() around the slice on purpose: a one-element slice comes back as a STRING in PowerShell,
        # and .Length then measures the word rather than the path.
        $tail = @(if ($segments.Length -gt 3) { $segments[3..($segments.Length - 1)] } else { @() })

        if ($tail.Length -eq 1 -and $tail[0] -eq 'issues') {
            if ($method -eq 'POST') {
                $doc = ConvertFrom-Json $body
                $issue = @{
                    number = $nextIssue; title = $doc.title; body = $doc.body; state = 'open'
                    labels = @(if ($doc.labels) { $doc.labels } else { @() })
                    # The real API stores the milestone it was given. A rig that always answered null
                    # would make the reconciler re-PATCH every card on every pass, which reads exactly
                    # like a broken idempotence bar.
                    milestone = $(if ($doc.PSObject.Properties.Name -contains 'milestone') { $doc.milestone } else { $null })
                }
                $issues[$nextIssue] = $issue
                $nextIssue++
                Write-Json $ctx (Serialize-Issue $issue) $null
            } else {
                if ($query -like '*page=1*') {
                    $all = ($issues.Values | ForEach-Object { Serialize-Issue $_ }) -join ','
                    Write-Json $ctx ('[' + $all + ']') $null
                } else {
                    Write-Json $ctx '[]' $null
                }
            }
            continue
        }
        if ($tail.Length -eq 1 -and $tail[0] -eq 'milestones') {
            if ($method -eq 'POST') {
                $title = (ConvertFrom-Json $body).title
                if (-not $milestones.ContainsKey($title)) { $milestones[$title] = $milestones.Count + 1 }
                Write-Json $ctx ('{"number":' + $milestones[$title] + ',"title":' + (ConvertTo-Json $title -Compress) + ',"state":"open"}') $null
            } else {
                if ($query -like '*page=1*') {
                    $all = ($milestones.GetEnumerator() | ForEach-Object {
                        '{"number":' + $_.Value + ',"title":' + (ConvertTo-Json $_.Key -Compress) + ',"state":"open"}' }) -join ','
                    Write-Json $ctx ('[' + $all + ']') $null
                } else {
                    Write-Json $ctx '[]' $null
                }
            }
            continue
        }
        if ($tail.Length -eq 2 -and $tail[0] -eq 'issues') {
            $number = [int]$tail[1]
            if ($method -eq 'GET') { Write-Json $ctx (Serialize-Issue $issues[$number]) $null; continue }
            $doc = ConvertFrom-Json $body
            if ($doc.PSObject.Properties.Name -contains 'state' -and $doc.state) { $issues[$number].state = $doc.state }
            if ($doc.PSObject.Properties.Name -contains 'title' -and $doc.title) { $issues[$number].title = $doc.title }
            if ($doc.PSObject.Properties.Name -contains 'body' -and $doc.body) { $issues[$number].body = $doc.body }
            if ($doc.PSObject.Properties.Name -contains 'labels' -and $doc.labels) { $issues[$number].labels = @($doc.labels) }
            if ($doc.PSObject.Properties.Name -contains 'milestone' -and $doc.milestone) { $issues[$number].milestone = $doc.milestone }
            Write-Json $ctx (Serialize-Issue $issues[$number]) $null
            continue
        }
        if ($tail.Length -eq 3 -and $tail[2] -eq 'comments') {
            if ($method -eq 'POST') { Write-Json $ctx '{"id":1,"body":"ok"}' $null } else { Write-Json $ctx '[]' $null }
            continue
        }

        Write-Json $ctx '{}' $null
    }

    $listener.Stop()
    $listener.Close()
}

# ---- the rig -------------------------------------------------------------------------------------

$rig = Join-Path $env:TEMP ('dv62rig-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $rig | Out-Null
'# dv6.2 rig' | Set-Content -Path (Join-Path $rig 'TRACKER.md') -Encoding ascii

$planJson = @"
{
  "name": "dv62-rig",
  "repo": "$($rig.Replace('\','\\'))",
  "tracker": "TRACKER.md",
  "agent": { "command": "git", "args": ["-p", "{prompt}"] },
  "limits": { "dnsHealthCheck": { "enabled": false }, "authPreflight": false },
  "stages": [ { "id": "S1", "title": "the only stage", "sessions": 1 } ]
}
"@
$planPath = Join-Path $rig 'rig.plan.json'
$planJson | Set-Content -Path $planPath -Encoding ascii

# A COPY of the run store. Never the live one: a fresh build must not open the running engine's
# database for write, and a proof must not depend on a run that is moving underneath it.
$storeCopy = Join-Path $rig 'run.db'
Copy-Item (Join-Path $repoRoot '.conductor\run.db') $storeCopy
foreach ($suffix in @('-wal', '-shm')) {
    $side = (Join-Path $repoRoot '.conductor\run.db') + $suffix
    if (Test-Path $side) { Copy-Item $side ($storeCopy + $suffix) }
}

Write-Output "rig       : $rig"
Write-Output "api base  : $base  (nothing can reach github.com)"

$job = Start-Job -ScriptBlock $server -ArgumentList $port
Start-Sleep -Milliseconds 800

try {
    $env:CONDUCTOR_GITHUB_API = $base
    $env:CONDUCTOR_GITHUB_TOKEN = 'rig-token-not-a-real-credential'
    $env:CONDUCTOR_PLAN = $null

    # Three passes, not two. With a clean ledger the board is stable after pass 1; with the DUPLICATE
    # rows this repo's followups.md actually carries, a row whose sibling created the issue only
    # becomes placeable on the NEXT pass, so the board settles on pass 3. Bug #81. A two-pass rig
    # would have shown the wobble and not the settle.
    foreach ($pass in 1, 2, 3) {
        Write-Output ''
        Write-Output "=== pass $pass - conductor github sync --project 7 (fresh build) ==="
        Push-Location $repoRoot
        dotnet run --project src/Conductor --no-build -- github sync `
            --backfill $storeCopy --project 7 --repo owner/scratch --no-diary -p $planPath 2>&1 |
            Select-Object -Last 14
        Write-Output "exit=$LASTEXITCODE"
        Pop-Location
        $tally = Invoke-RestMethod -Uri "$base/__mutations" -Method Get
        Write-Output ("after pass {0}: {1} mutations, {2} board items" -f $pass, $tally.mutations, $tally.items)
    }
} finally {
    # State first, shutdown second: the listener closes its socket the moment it breaks out, and a
    # reply read across that close is a transport error rather than a result.
    $state = Invoke-RestMethod -Uri "$base/__state" -Method Get
    try { Invoke-RestMethod -Uri "$base/__shutdown" -Method Get -TimeoutSec 5 | Out-Null } catch { }
    Remove-Job $job -Force
    Remove-Item Env:\CONDUCTOR_GITHUB_API -ErrorAction SilentlyContinue
    Remove-Item Env:\CONDUCTOR_GITHUB_TOKEN -ErrorAction SilentlyContinue
}

Write-Output ''
Write-Output '=== the board the engine actually wrote ==='
Write-Output ("issues created : " + $state.issues.Count)
Write-Output ("board items    : " + $state.items.Count)
Write-Output ("mutations      : " + $state.mutations + "  (all passes together)")
Write-Output '--- columns ---'
$state.items | Group-Object -Property column | Sort-Object Name | ForEach-Object {
    Write-Output ("  {0,-12} {1}" -f $_.Name, $_.Count)
}
Write-Output '--- the graphql documents the engine put on the wire ---'
$state.documents | Select-Object -Unique | ForEach-Object { Write-Output ("  " + $_) }
