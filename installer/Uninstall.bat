@echo off
rem Double-click to remove TUNIC Seamless Co-op (optionally BepInEx too). Saves are not touched.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall %*
