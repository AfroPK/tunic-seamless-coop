# Test driver for the TunicCoop autopilot. Dot-source it:  . .\tools\coop.ps1
# Targets: "A" = this PC (host), "B" = second PC over SSH, "C" = a second game instance on the second PC
# (its own copy C:\Games\TUNIC_P3, so it has its own BepInEx config/commands; used for 3-player tests).
# Machine-specific settings live in tools/rig.json (git-ignored); see tools/rig.example.json.
$rigFile = Join-Path $PSScriptRoot 'rig.json'
if (-not (Test-Path $rigFile)) { $rigFile = Join-Path $PSScriptRoot 'rig.example.json' }
$rig = Get-Content $rigFile -Raw | ConvertFrom-Json
$Script:Remote   = $rig.Remote
$Script:Games    = @{ A = $rig.Games.A; B = $rig.Games.B; C = $rig.Games.C }
$Script:Dirs     = @{}; foreach ($k in $Script:Games.Keys) { $Script:Dirs[$k] = "$($Script:Games[$k])\BepInEx\coop_test" }
$Script:SeqFile  = Join-Path $PSScriptRoot '.seq'
$Script:ShotsOut = Join-Path $PSScriptRoot '..\test_shots'
$Script:Ssh      = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10')
$Script:Ps       = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -Command'

function IsLocal($Pc) { $Pc -eq 'A' }
function Fwd($p) { $p -replace '\\', '/' }

function Next-Seq {
    $n = 1000
    if (Test-Path $Script:SeqFile) { $n = [int](Get-Content $Script:SeqFile) }
    $n++; Set-Content $Script:SeqFile $n; $n
}

function Invoke-B([string]$cmd) { ssh @Script:Ssh $Script:Remote $cmd 2>&1 }

# Send one command; returns its seq.
function Send-Cmd([ValidateSet('A','B','C')]$Pc, [string]$Line) {
    $seq = Next-Seq
    $full = "$seq $Line"
    if (IsLocal $Pc) {
        New-Item -ItemType Directory -Force $Script:Dirs[$Pc] | Out-Null
        Add-Content -Path (Join-Path $Script:Dirs[$Pc] 'cmd.txt') -Value $full
    } else {
        # NB cmd.exe: in `if ... mkdir X & echo` the `& echo` belongs to the if-body, so keep them separate.
        # cmd.exe metacharacters (| & < > ^ and parentheses inside the echo group) must be ^-escaped.
        $esc = $full -replace '([\^|&<>()])', '^$1'
        Invoke-B "(echo $esc)>>`"$($Script:Dirs[$Pc])\cmd.txt`"" | Out-Null
    }
    $seq
}

function Read-Results([ValidateSet('A','B','C')]$Pc) {
    $p = Join-Path $Script:Dirs[$Pc] 'results.txt'
    if (IsLocal $Pc) { if (Test-Path $p) { Get-Content $p } } else { Invoke-B "type `"$p`"" }
}

# Wait for a seq to finish; returns its result line.
function Wait-Seq([ValidateSet('A','B','C')]$Pc, [int]$Seq, [int]$TimeoutSec = 60) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $hit = Read-Results $Pc | Where-Object { $_ -match "^$Seq " } | Select-Object -Last 1
        if ($hit) { return $hit }
        Start-Sleep -Milliseconds 500
    }
    "$Seq TIMEOUT (no result after ${TimeoutSec}s)"
}

function Do-Cmd([ValidateSet('A','B','C')]$Pc, [string]$Line, [int]$TimeoutSec = 60) {
    $s = Send-Cmd $Pc $Line
    "[$Pc] $(Wait-Seq $Pc $s $TimeoutSec)"
}

function Get-State([ValidateSet('A','B','C')]$Pc) {
    $p = Join-Path $Script:Dirs[$Pc] 'state.json'
    $raw = if (IsLocal $Pc) { Get-Content $p -Raw -EA SilentlyContinue } else { (Invoke-B "type `"$p`"") -join '' }
    try { $raw | ConvertFrom-Json } catch { $null }
}

# Take a screenshot and bring it here (plus a downscaled copy for reading).
function Get-Shot([ValidateSet('A','B','C')]$Pc, [string]$Name) {
    $null = Do-Cmd $Pc "shot $Name" 30
    New-Item -ItemType Directory -Force $Script:ShotsOut | Out-Null
    $dst = Join-Path $Script:ShotsOut "$Pc-$Name.png"
    $src = Join-Path $Script:Dirs[$Pc] "shots\$Name.png"
    if (IsLocal $Pc) { Copy-Item $src $dst -Force } else { scp -q @Script:Ssh "$($Script:Remote):$(Fwd $src)" $dst }
    $small = $dst -replace '\.png$', '_s.jpg'
    ffmpeg -v error -y -i $dst -vf "scale=960:-1" -q:v 4 $small
    $small
}

function Get-Log([ValidateSet('A','B','C')]$Pc, [string]$Pattern = '\[(net|coop|puppet|world|inv|smash|coin|auto|diag|sync|gui|mon|chest)\]|Exception', [int]$Last = 40) {
    $log = "$($Script:Games[$Pc])\BepInEx\LogOutput.log"
    if (IsLocal $Pc) { Select-String $log -Pattern $Pattern | ForEach-Object Line | Select-Object -Last $Last }
    else { Invoke-B "$($Script:Ps) `"Get-Content '$log' -Tail 6000`"" | Select-String -Pattern $Pattern | ForEach-Object Line | Select-Object -Last $Last }
}

# Two instances can run on the second PC: tell them apart by executable path.
function Test-Running([ValidateSet('A','B','C')]$Pc) {
    $exe = "$($Script:Games[$Pc])\Tunic.exe"
    # Locally the sandbox can hide Process.Path (null) → fall back to the name; only one instance runs here.
    if (IsLocal $Pc) { [bool](Get-Process tunic -EA SilentlyContinue | Where-Object { -not $_.Path -or $_.Path -eq $exe }) }
    else { [bool]((Invoke-B "$($Script:Ps) `"(Get-Process tunic -EA SilentlyContinue | ? { `$_.Path -eq '$exe' }).Id`"") -match '\d') }
}

function Start-Game([ValidateSet('A','B','C')]$Pc) {
    # Windowed: exclusive fullscreen stalls Unity when the window isn't focused, freezing unattended tests.
    $args = '-screen-fullscreen 0 -screen-width 1280 -screen-height 720'
    $exe = "$($Script:Games[$Pc])\Tunic.exe"
    if (IsLocal $Pc) { Start-Process $exe -ArgumentList $args -WorkingDirectory $Script:Games[$Pc]; return }
    # SSH sessions can't show windows. One-shot task in the logged-in desktop session (/it):
    # create → run → delete immediately, so nothing persists. (Date format on B: yyyy/mm/dd.)
    $launch = "$($Script:Dirs[$Pc])\launch.cmd"
    Invoke-B "if not exist `"$($Script:Dirs[$Pc])`" mkdir `"$($Script:Dirs[$Pc])`"" | Out-Null
    Invoke-B "(echo @start `"`" `"$exe`" $args)>`"$launch`"" | Out-Null
    Invoke-B "schtasks /create /tn TunicCoopLaunchOnce /tr `"$launch`" /sc once /st 23:59 /sd 2030/01/01 /it /f >nul && schtasks /run /tn TunicCoopLaunchOnce >nul & ping -n 4 127.0.0.1 >nul & schtasks /delete /tn TunicCoopLaunchOnce /f >nul" | Out-Null
}

function Stop-Game([ValidateSet('A','B','C')]$Pc) {
    if (-not (Test-Running $Pc)) { return 'not running' }
    $null = Send-Cmd $Pc 'quit'
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) { if (-not (Test-Running $Pc)) { return 'stopped' }; Start-Sleep 1 }
    if (-not (IsLocal $Pc)) {
        $exe = "$($Script:Games[$Pc])\Tunic.exe"
        Invoke-B "$($Script:Ps) `"Get-Process tunic -EA SilentlyContinue | ? { `$_.Path -eq '$exe' } | Stop-Process -Force`"" | Out-Null
        return 'killed'
    }
    'still running'
}

function Boot-Count([ValidateSet('A','B','C')]$Pc) { @(Read-Results $Pc | Where-Object { $_ -like '# boot*' }).Count }

# Launch and wait until the autopilot reports a new "# boot" line.
function Start-AndWait([ValidateSet('A','B','C')]$Pc, [int]$TimeoutSec = 150) {
    $n = Boot-Count $Pc
    Start-Game $Pc
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) { Start-Sleep 3; if ((Boot-Count $Pc) -gt $n) { return $true } }
    $false
}

# Install the current build. Running game: stage TunicCoop.dll.next + "restart" (the mod swaps it in and
# relaunches itself inside the desktop session). Not running: copy and launch. Returns after the new boot.
function Update-Game([ValidateSet('A','B','C')]$Pc) {
    $src = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\TunicCoop\bin\Release\TunicCoop.dll')
    $h = (Get-FileHash $src).Hash.Substring(0, 12)
    if (-not (Test-Running $Pc)) { Deploy $Pc | Out-Null; $ok = Start-AndWait $Pc; return "$Pc fresh-launched $h boot=$ok" }
    $n = Boot-Count $Pc
    $staged = "$($Script:Games[$Pc])\BepInEx\plugins\TunicCoop\TunicCoop.dll.next"
    if (IsLocal $Pc) { Copy-Item $src $staged -Force } else { scp -q @Script:Ssh $src "$($Script:Remote):$(Fwd $staged)" }
    $null = Send-Cmd $Pc 'restart'
    $deadline = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $deadline) { Start-Sleep 3; if ((Boot-Count $Pc) -gt $n) { return "$Pc restarted on $h" } }
    "$Pc restart TIMEOUT ($h)"
}

# Fresh co-op session: update both to the current build, load saves, A hosts, B joins, optional regroup.
function Reset-Session([double[]]$At = $null) {
    Update-Game A; Update-Game B
    Do-Cmd A 'continue 60' 70; Do-Cmd B 'continue 60' 70
    Do-Cmd A 'waitplayer 60' 70; Do-Cmd B 'waitplayer 60' 70
    # Tests start in the Overworld. A save that last autosaved in a boss arena loads there: warp back first
    # (to the starting-island teleporter pad), or "beach" coordinates would be void in that area.
    foreach ($p in 'A', 'B') {
        if ((Get-State $p).scene -ne 'Overworld Redux') {
            Do-Cmd $p 'warp Overworld Redux|Transit_teleporter_starting island' 60
            Do-Cmd $p 'waitplayer 40' 45
        }
    }
    # Default host rules on both (rule tests change them, and config persists between runs).
    foreach ($p in 'A', 'B') { foreach ($r in 'revive on', 'reviveSeconds 3', 'reviveHealth 50', 'bleedOut 0', 'enemyHealth 0', 'share on', 'crawl off') { Do-Cmd $p "rule $r" 5 | Out-Null } }
    Do-Cmd A 'host' 10; Do-Cmd B 'join' 10
    Do-Cmd B 'waitplayers 2 20' 30; Do-Cmd A 'waitplayers 2 20' 30
    if ($At) {
        Do-Cmd A ("tp {0} {1} {2}" -f $At[0], $At[1], $At[2]) 10
        Do-Cmd B ("tp {0} {1} {2}" -f ($At[0] + 2), $At[1], $At[2]) 10
    }
}

function Deploy([ValidateSet('A','B','C','both','all')]$Pc = 'both') {
    $src = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\TunicCoop\bin\Release\TunicCoop.dll')
    $h = (Get-FileHash $src).Hash.Substring(0, 12)
    $targets = switch ($Pc) { 'both' { 'A', 'B' } 'all' { 'A', 'B', 'C' } default { $Pc } }
    foreach ($t in $targets) {
        $dst = "$($Script:Games[$t])\BepInEx\plugins\TunicCoop\TunicCoop.dll"
        if (IsLocal $t) { Copy-Item $src $dst -Force } else { scp -q @Script:Ssh $src "$($Script:Remote):$(Fwd $dst)" }
        "$t deployed $h"
    }
}

# Compile the mod; throws (stopping the pipeline) on errors.
function Build {
    $out = dotnet build (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\TunicCoop') -c Release -nologo -v q 2>&1
    $err = @($out | Select-String ' error ')
    if ($LASTEXITCODE -ne 0 -or $err.Count) { $err | Select-Object -First 10 | ForEach-Object { $_.Line }; throw 'build failed' }
    'build ok'
}

# Copy TUNIC's SAVES folder (both PCs) to tools/savebackups/<pc>/<timestamp> before tests touch the saves.
function Backup-Saves {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $root = Join-Path $PSScriptRoot 'savebackups'
    $src = "$env:USERPROFILE\AppData\LocalLow\Andrew Shouldice\Secret Legend\SAVES"
    $dst = Join-Path $root "A\$stamp"; New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item "$src\*" $dst -Force
    "A: $(@(Get-ChildItem $dst).Count) files -> $dst"
    $zip = "tunic_saves_$stamp.zip"
    Invoke-B "tar -a -c -f `"%TEMP%\$zip`" -C `"%USERPROFILE%\AppData\LocalLow\Andrew Shouldice\Secret Legend\SAVES`" ." | Out-Null
    $dstB = Join-Path $root 'B'; New-Item -ItemType Directory -Force $dstB | Out-Null
    scp -q @Script:Ssh "$($Script:Remote):AppData/Local/Temp/$zip" (Join-Path $dstB $zip)
    "B: $(if (Test-Path (Join-Path $dstB $zip)) { 'saved' } else { 'FAILED' }) -> $dstB\$zip"}
