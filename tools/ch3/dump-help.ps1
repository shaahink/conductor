# CH3.1 - dump a conductor binary's whole CLI surface, one file per top-level
# verb, with every sub-verb's help appended to the same file.
# Usage: tools\ch3\dump-help.ps1 <path-to-conductor.exe> <output-dir>
# ASCII only (Windows PowerShell 5.1).
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-ChildItem $OutDir -Filter *.txt | Remove-Item -Force

$top = & $Exe --help 2>&1 | Out-String -Width 400
Set-Content -Path (Join-Path $OutDir '_top.txt') -Value $top

# The verb column of the top-level help: first token of each COMMANDS row.
$verbs = @()
$inCommands = $false
foreach ($line in ($top -split "`r?`n")) {
    if ($line -match '^COMMANDS:') { $inCommands = $true; continue }
    if (-not $inCommands) { continue }
    if ($line -match '^    ([a-z][a-z0-9-]*)\s') { $verbs += $Matches[1] }
}

# Sub-verbs are not in the parser's help tree, so they are named here.
$subs = @{
    'plan'      = @('new', 'set', 'reload', 'add-stage', 'import')
    'bug'       = @('new', 'list', 'fix')
    'github'    = @('sync', 'backfill', 'ci', 'sarif')
    'inbox'     = @('list', 'show', 'add', 'transcribe', 'parked', 'prune')
    'courier'   = @('status', 'run', 'install', 'uninstall', 'restart', 'stop', 'allow', 'deny', 'chat', 'unchat')
    'bg'        = @('start', 'status', 'logs', 'stop')
    'catalogue' = @('repair')
    'run'       = @('close', 'adopt')
    'history'   = @('export')
}

Push-Location $env:TEMP        # no plan resolves here, so no state is touched
$env:CONDUCTOR_PLAN = $null
try {
    foreach ($v in $verbs) {
        $out = & $Exe $v --help 2>&1 | Out-String -Width 400
        Set-Content -Path (Join-Path $OutDir "$v.txt") -Value $out
        if ($subs.ContainsKey($v)) {
            foreach ($s in $subs[$v]) {
                $sub = & $Exe $v $s --help 2>&1 | Out-String -Width 400
                Add-Content -Path (Join-Path $OutDir "$v.txt") -Value "`n===== SUB $v $s =====`n$sub"
            }
        }
    }
}
finally { Pop-Location }
Write-Output ("dumped {0} verbs to {1}" -f $verbs.Count, $OutDir)
