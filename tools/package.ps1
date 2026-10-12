# Build the release packages:
#   dist\TunicCoop-<version>.zip              extract into the TUNIC folder, or run Install.bat from it
$root = Split-Path $PSScriptRoot -Parent
$ver = ([regex]::Match((Get-Content "$root\src\TunicCoop\Plugin.cs" -Raw), 'Version = "([^"]+)"')).Groups[1].Value
& "C:\Program Files\dotnet\dotnet.exe" build "$root\src\TunicCoop" -c Release | Select-String ' error |Build succeeded'
$bin = "$root\src\TunicCoop\bin\Release"
$thirdParty = @"
Third-party components bundled with TUNIC Seamless Co-op

LiteNetLib (LiteNetLib.dll)
  https://github.com/RevenantX/LiteNetLib
  MIT License, Copyright (c) 2020 Ruslan Pyrch

Not bundled: BepInEx 6.0.0-pre.1 (LGPL-2.1, https://github.com/BepInEx/BepInEx). Install.bat downloads the
official release from GitHub and verifies its SHA-256 before installing it.
"@

# --- regular zip
$stage = "$root\dist\stage"
Remove-Item $stage -Recurse -Force -EA SilentlyContinue
$plug = New-Item -ItemType Directory -Force "$stage\BepInEx\plugins\TunicCoop"
Copy-Item "$bin\TunicCoop.dll", "$bin\LiteNetLib.dll" $plug
Copy-Item "$root\README.md", "$root\LICENSE", "$root\installer\Install.bat", "$root\installer\install.ps1" $stage
$thirdParty | Set-Content "$stage\THIRD_PARTY.txt"
$zip = "$root\dist\TunicCoop-$ver.zip"
Remove-Item $zip -EA SilentlyContinue
Compress-Archive -Path "$stage\*" -DestinationPath $zip

Get-Item $zip | Select-Object Name, Length
