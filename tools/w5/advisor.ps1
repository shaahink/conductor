# W5.1 rehearsal advisor -- a token-free stand-in for the advisor model.
#
# The advisor is only ever consulted where the owner asks for one (import, refine, split, judging a
# stuck stage) -- never inside scheduling. The rehearsal exercises the SPLIT contract, so this
# answers that one and stays deliberately unhelpful elsewhere: an advisor that invented verdicts
# would let the rehearsal pass on the strength of the harness rather than the engine.
#
# ASCII ONLY (see tools/w5/agent.ps1 for why).
param([string]$Prompt = "")
$ErrorActionPreference = "Stop"

if ($Prompt -match '"subtasks"') {
    Write-Output '{"subtasks":[{"title":"the first half of the new requirement","context":"start from the existing work.txt"},{"title":"the second half of the new requirement","context":""}]}'
    exit 0
}

# Nothing else is part of this rehearsal's contract. Say so in a shape no parser accepts as a
# verdict, so a surprise consultation shows up as a surprise instead of a pass.
Write-Output 'the W5.1 rehearsal advisor only answers split proposals'
exit 0
