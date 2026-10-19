# Builds a release in dist\:
#   VoidCrewModLauncher-Setup.exe            the one-click install: one self-contained exe (no .NET needed) that installs
#                                            itself when double-clicked, with the bridge plugin inside it
#   Void_Crew_Mod_Launcher-<version>.zip     the Thunderstore upload: manifest.json, icon.png, README.md, CHANGELOG.md
#                                            and the setup exe at its root
# Usage: powershell -ExecutionPolicy Bypass -File tools\package.ps1 [-VoidCrewDir "<game folder>"] [-BepInExDir "<BepInEx folder>"]
# The bridge plugin builds against your installed game and BepInEx (see src\VoidCrewModLauncher.Bridge\*.csproj).
param(
    [string]$VoidCrewDir = "",
    [string]$BepInExDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\VoidCrewModLauncher\VoidCrewModLauncher.csproj"
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$manifest = Get-Content (Join-Path $root "thunderstore\manifest.json") -Raw | ConvertFrom-Json
if ($manifest.version_number -ne $version) {
    throw "thunderstore\manifest.json says $($manifest.version_number) but Directory.Build.props says $version; make them match."
}
$plugin = Select-String -Path (Join-Path $root "src\VoidCrewModLauncher.Bridge\Plugin.cs") -Pattern 'const string Version = "([^"]+)"'
if ($plugin.Matches[0].Groups[1].Value -ne $version) {
    throw "The bridge's Plugin.cs says $($plugin.Matches[0].Groups[1].Value) but Directory.Build.props says $version; make them match."
}

$properties = @("-p:Configuration=Release")
if ($VoidCrewDir) { $properties += "-p:VoidCrewDir=$VoidCrewDir" }
if ($BepInExDir) { $properties += "-p:BepInExDir=$BepInExDir" }

# The bridge first (the launcher carries it inside), then the launcher as one self-contained file.
dotnet build (Join-Path $root "src\VoidCrewModLauncher.Bridge\VoidCrewModLauncher.Bridge.csproj") @properties -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "The bridge plugin didn't build." }

$publish = Join-Path ([System.IO.Path]::GetTempPath()) "VoidCrewModLauncher-publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project @properties -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $publish -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Publishing the launcher failed." }

$dist = Join-Path $root "dist"
New-Item -ItemType Directory $dist -Force | Out-Null
$setup = Join-Path $dist "VoidCrewModLauncher-Setup.exe"
Copy-Item (Join-Path $publish "VoidCrewModLauncher.exe") $setup -Force

$stage = Join-Path ([System.IO.Path]::GetTempPath()) "VoidCrewModLauncher-package"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item (Join-Path $root "thunderstore\manifest.json"), (Join-Path $root "thunderstore\icon.png"), (Join-Path $root "thunderstore\README.md"), (Join-Path $root "CHANGELOG.md"), $setup -Destination $stage
$zip = Join-Path $dist "Void_Crew_Mod_Launcher-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage, $publish -Recurse -Force

Write-Output ("Packed {0} ({1:N1} MB) and {2} ({3:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB), $zip, ((Get-Item $zip).Length / 1MB))
