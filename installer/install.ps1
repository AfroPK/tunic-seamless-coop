# TUNIC Seamless Co-op installer.
# Finds TUNIC (Steam, Epic, GOG or a folder you pick), installs BepInEx 6.0.0-pre.1 (IL2CPP x64) if it isn't
# there yet, and copies the mod. Run it with Install.bat (double-click), or:  powershell -ExecutionPolicy Bypass -File install.ps1
param([string]$GameDir, [switch]$NoPause, [switch]$Uninstall, [switch]$RemoveBepInEx)

$ErrorActionPreference = 'Stop'
$BepUrl  = 'https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.1/BepInEx_UnityIL2CPP_x64_6.0.0-pre.1.zip'
$BepHash = 'B5AC213EE70B029BDA1353A82B067D89709EC1A55EC9B775779C2813F3D84212'   # SHA-256 of the official release zip
$Here    = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say($t, $c = 'Gray') { Write-Host $t -ForegroundColor $c }

function Find-Tunic {
    $found = New-Object System.Collections.Generic.List[string]
    # Steam: every library folder
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
        $libs = @($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) { $libs += [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' } }
        foreach ($l in $libs | Select-Object -Unique) { $p = Join-Path $l 'steamapps\common\TUNIC'; if (Test-Path (Join-Path $p 'Tunic.exe')) { $found.Add($p) } }
    } catch { }
    # Epic: launcher manifests
    try {
        Get-ChildItem 'C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item' -ErrorAction Stop | ForEach-Object {
            $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
            if ($j.DisplayName -match 'TUNIC' -and (Test-Path (Join-Path $j.InstallLocation 'Tunic.exe'))) { $found.Add($j.InstallLocation) }
        }
    } catch { }
    # GOG: registry
    foreach ($k in 'HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games', 'HKLM:\SOFTWARE\GOG.com\Games') {
        try {
            Get-ChildItem $k -ErrorAction Stop | ForEach-Object {
                $g = Get-ItemProperty $_.PSPath
                if ($g.gameName -match 'TUNIC' -and (Test-Path (Join-Path $g.path 'Tunic.exe'))) { $found.Add($g.path) }
            }
        } catch { }
    }
    # Common default folders
    foreach ($p in 'C:\Program Files\Epic Games\TUNIC', 'C:\Program Files (x86)\GOG Galaxy\Games\TUNIC', 'C:\GOG Games\TUNIC') {
        if (Test-Path (Join-Path $p 'Tunic.exe')) { $found.Add($p) }
    }
    $found | Select-Object -Unique
}

Say ''
Say '  TUNIC Seamless Co-op - installer' 'Yellow'
Say ''

if (-not $GameDir) {
    $dirs = @(Find-Tunic)
    if ($dirs.Count -eq 1) { $GameDir = $dirs[0]; Say "Found TUNIC: $GameDir" }
    elseif ($dirs.Count -gt 1) {
        Say 'Found TUNIC in more than one place:'
        for ($i = 0; $i -lt $dirs.Count; $i++) { Say "  [$($i + 1)] $($dirs[$i])" }
        $n = Read-Host 'Which one? (number)'
        $GameDir = $dirs[[int]$n - 1]
    } else {
        Say "Couldn't find TUNIC automatically."
        $GameDir = Read-Host 'Paste the folder that contains Tunic.exe'
    }
}
$GameDir = $GameDir.Trim('"', ' ')
if (-not (Test-Path (Join-Path $GameDir 'Tunic.exe'))) { Say "No Tunic.exe in '$GameDir'. Nothing was changed." 'Red'; exit 1 }
if (Get-Process Tunic -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith((Resolve-Path $GameDir).Path, [StringComparison]::OrdinalIgnoreCase) }) { Say 'Please close TUNIC first, then run the installer again.' 'Red'; exit 1 }

if ($Uninstall) {
    # Remove the mod (and its settings). Your saves are untouched.
    $mod = Join-Path $GameDir 'BepInEx\plugins\TunicCoop'
    $cfg = Join-Path $GameDir 'BepInEx\config\com.freddy.tuniccoop.cfg'
    if (Test-Path $mod) { Remove-Item $mod -Recurse -Force; Say "Removed $mod" 'Green' } else { Say 'The mod was not installed in this folder.' }
    if (Test-Path $cfg) { Remove-Item $cfg -Force; Say 'Removed the mod settings (com.freddy.tuniccoop.cfg).' }

    # BepInEx itself: only if nothing else uses it.
    $others = @(Get-ChildItem (Join-Path $GameDir 'BepInEx\plugins') -ErrorAction SilentlyContinue)
    $bepFiles = 'BepInEx', 'mono', 'winhttp.dll', 'doorstop_config.ini', 'changelog.txt' | ForEach-Object { Join-Path $GameDir $_ } | Where-Object { Test-Path $_ }
    if ($bepFiles.Count -gt 0) {
        $remove = $RemoveBepInEx
        if (-not $remove) {
            if ($others.Count -gt 0) { Say "Other BepInEx mods are still installed ($(($others | ForEach-Object Name) -join ', ')), so BepInEx is kept." }
            else { $remove = (Read-Host 'Also remove BepInEx (the mod loader) to get vanilla TUNIC back? (y/N)') -match '^[yY]' }
        }
        if ($remove) {
            foreach ($p in $bepFiles) { Remove-Item $p -Recurse -Force }
            Say 'Removed BepInEx. TUNIC is back to vanilla.' 'Green'
        } elseif ($others.Count -eq 0) { Say 'BepInEx kept (it does nothing without mods).' }
    }
    Say ''
    Say 'Done. Your saves were not touched.'
    if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Read-Host 'Press Enter to close' | Out-Null }
    exit 0
}

# 1) BepInEx
$bepCore = Join-Path $GameDir 'BepInEx\core\BepInEx.IL2CPP.dll'
if (Test-Path $bepCore) {
    Say 'BepInEx is already installed.' 'Green'
} else {
    Say 'Downloading BepInEx 6.0.0-pre.1 (IL2CPP x64) from github.com/BepInEx ...'
    $tmp = Join-Path $env:TEMP 'BepInEx_UnityIL2CPP_x64_6.0.0-pre.1.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $BepUrl -OutFile $tmp -UseBasicParsing
    $h = (Get-FileHash $tmp -Algorithm SHA256).Hash
    if ($h -ne $BepHash) { Remove-Item $tmp -Force; Say "The download doesn't match the official BepInEx release (hash $h). Nothing was changed." 'Red'; exit 1 }
    Expand-Archive -Path $tmp -DestinationPath $GameDir -Force
    Remove-Item $tmp -Force
    Say 'BepInEx installed (verified against the official release).' 'Green'
}

# 2) The mod
$src = Join-Path $Here 'BepInEx\plugins\TunicCoop'
if (-not (Test-Path (Join-Path $src 'TunicCoop.dll'))) { Say "Can't find BepInEx\plugins\TunicCoop next to this installer. Extract the whole zip first." 'Red'; exit 1 }
$dst = Join-Path $GameDir 'BepInEx\plugins\TunicCoop'
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item (Join-Path $src '*') $dst -Force
Say "Mod installed to $dst" 'Green'

Say ''
Say 'Done! Start TUNIC. The very first start with BepInEx takes a minute or two (it prepares the game once).'
Say 'In game, press F1 to pick your name and host or join a session.'
Say ''
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Read-Host 'Press Enter to close' | Out-Null }
