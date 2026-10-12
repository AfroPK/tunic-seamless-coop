# Full regression on both PCs; results -> tools/last-run.txt
. "$PSScriptRoot\coop.ps1"; . "$PSScriptRoot\suite.ps1"
$out = "$PSScriptRoot\last-run.txt"
"run started $(Get-Date -Format o)" | Set-Content $out
Backup-Saves | Add-Content $out          # tests write to the save slot they load: keep a copy first
$tests = 'Test-Combat', 'Test-Aggro', 'Test-Coins', 'Test-Chest90', 'Test-Scenes', 'Test-Death', 'Test-Revive',
         'Test-HostRules', 'Test-Scaling', 'Test-Signals', 'Test-SharedCam', 'Test-Regroup', 'Test-Connect', 'Test-Rejoin',
         'Test-Gauntlet', 'Test-Bosses', 'Test-Relay', 'Test-ThreePlayers'
foreach ($t in $tests) {
    "--- $t" | Add-Content $out
    try {
        Reset-Session | Out-Null
        if ($t -eq 'Test-ThreePlayers') { Update-Game C | Out-Null }
        if ($t -eq 'Test-Relay') {
            # the relay from server/ runs on this PC; the players reach it over the LAN
            if (-not (Get-NetUDPEndpoint -LocalPort 27016 -ErrorAction SilentlyContinue)) { Start-Process "$PSScriptRoot\..\dist\relay-win-x64\TunicCoopRelay.exe" -ArgumentList 27016 -WindowStyle Hidden }
            $ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '192.168.*' -and $_.InterfaceAlias -notlike 'vEthernet*' } | Select-Object -First 1).IPAddress
            Test-Relay "$($ip):27016" 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -match '^(PASS|FAIL|SKIP)' } | ForEach-Object { $_ -replace '\d+\.\d+\.\d+\.\d+', '<ip>' } | Add-Content $out
            continue
        }
        & $t 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -match '^(PASS|FAIL|SKIP)' } | Add-Content $out
    } catch { "FAIL $t threw: $($_.Exception.Message)" | Add-Content $out }
}
$all = Get-Content $out
"TOTAL: $(@($all | Where-Object { $_ -like 'PASS*' }).Count) passed, $(@($all | Where-Object { $_ -like 'FAIL*' }).Count) failed" | Add-Content $out
"run finished $(Get-Date -Format o)" | Add-Content $out
