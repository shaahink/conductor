# Session 36 (SF6 fix) - the suites that assert prompt prose, plus the scratch budget probe.
# A script rather than an inline command because `conductor bg` hands the command to cmd.exe, which
# eats the `|` an xunit OR-filter needs. Pass -All to run the whole suite instead.
param([switch]$All)
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $here)
if (-not $root) { $root = (Get-Location).Path }
Set-Location $root
if ($All) {
    dotnet test Conductor.slnx
} else {
    $filter = 'FullyQualifiedName~SF6_1TemplateLessonsTests|FullyQualifiedName~SF6_2PromptBankTests|FullyQualifiedName~SF6_3InitScaffoldTests|FullyQualifiedName~SC4_4Tests|FullyQualifiedName~SF0_3PidsAndBackgroundWorkTests|FullyQualifiedName~W2OnePromptTests|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~ZzBudgetProbe'
    dotnet test Conductor.slnx --filter $filter
}
exit $LASTEXITCODE
