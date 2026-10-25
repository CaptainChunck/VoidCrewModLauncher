# Draws the README's screenshots (docs/screenshots/*.png) with sample lobbies and mods, off screen.
# Usage: powershell -ExecutionPolicy Bypass -File tools\screenshots.ps1   (after building the launcher)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root "src\VoidCrewModLauncher\bin") -Recurse -Filter VoidCrewModLauncher.exe |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "Build the launcher first (dotnet build src\VoidCrewModLauncher)." }
# A folder of its own for each run (two runs at once don't share one).
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("vcml-screenshots-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
& $exe.FullName --render-previews $temp | Out-Null
$process = Get-Process VoidCrewModLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe.FullName }
if ($process) { $process | Wait-Process -Timeout 240 }
$names = @{
    "1-browser-not-running.png" = "server-browser-start.png"
    "2-browser-live.png" = "server-browser.png"
    "3-mods-installed.png" = "mod-manager-installed.png"
    "4-mods-browse.png" = "mod-manager-mods.png"
    "5-host.png" = "host-lobby.png"
    "6-settings.png" = "settings.png"
    "7-join-dialog.png" = "join-lobby.png"
}
$out = Join-Path $root "docs\screenshots"
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($name in $names.Keys) {
    Copy-Item (Join-Path $temp $name) (Join-Path $out $names[$name]) -Force
}
if ((Get-Content (Join-Path $temp "bindings.log") -ErrorAction SilentlyContinue | Measure-Object -Line).Lines -gt 0) {
    # Kept for reading; a clean run's folder is deleted.
    Write-Warning "Some bindings failed; see $temp\bindings.log"
}
else {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output "Screenshots written to $out"
