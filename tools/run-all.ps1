# Full regression: every test from a fresh session. Results → tools\last-run.txt
. "$PSScriptRoot\suite.ps1"
$out = Join-Path $PSScriptRoot 'last-run.txt'
"run started $(Get-Date -Format o)" | Set-Content $out
foreach ($t in 'Test-Combat', 'Test-Aggro', 'Test-Coins', 'Test-Chest90', 'Test-Scenes', 'Test-Death', 'Test-Revive', 'Test-ThreePlayers') {
    "--- $t" | Add-Content $out
    try {
        Reset-Session | Out-Null
        if ($t -eq 'Test-ThreePlayers') { Update-Game C | Out-Null }
        & $t | Out-Null
    } catch { "ERROR in ${t}: $_" | Add-Content $out }
}
"=== SUMMARY" | Add-Content $out
$Script:Results | Add-Content $out
"PASS $(@($Script:Results | Where-Object { $_ -like 'PASS*' }).Count) / $($Script:Results.Count)" | Add-Content $out
"run finished $(Get-Date -Format o)" | Add-Content $out
