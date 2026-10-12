# Build the release zip: dist\TunicCoop-<version>.zip, laid out to extract straight into the TUNIC folder.
$root = Split-Path $PSScriptRoot -Parent
$ver = ([regex]::Match((Get-Content "$root\src\TunicCoop\Plugin.cs" -Raw), 'Version = "([^"]+)"')).Groups[1].Value
& "C:\Program Files\dotnet\dotnet.exe" build "$root\src\TunicCoop" -c Release | Select-String 'error|Build succeeded'
$stage = "$root\dist\stage"
Remove-Item $stage -Recurse -Force -EA SilentlyContinue
$plug = New-Item -ItemType Directory -Force "$stage\BepInEx\plugins\TunicCoop"
Copy-Item "$root\src\TunicCoop\bin\Release\TunicCoop.dll", "$root\src\TunicCoop\bin\Release\LiteNetLib.dll" $plug
Copy-Item "$root\README.md", "$root\LICENSE" $stage
@"
Third-party components bundled with TUNIC Seamless Co-op

LiteNetLib (BepInEx/plugins/TunicCoop/LiteNetLib.dll)
  https://github.com/RevenantX/LiteNetLib
  MIT License, Copyright (c) 2020 Ruslan Pyrch

Not bundled (install separately): BepInEx 6.0.0-pre.1, LGPL-2.1, https://github.com/BepInEx/BepInEx
"@ | Set-Content "$stage\THIRD_PARTY.txt"
$zip = "$root\dist\TunicCoop-$ver.zip"
Remove-Item $zip -EA SilentlyContinue
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Get-Item $zip | Select-Object Name, Length
