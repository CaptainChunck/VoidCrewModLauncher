# Builds a release in dist\:
#   VoidCrewModLauncher-Setup.exe            the one-click install: one self-contained exe (no .NET needed) that installs
#                                            itself when double-clicked, with the bridge plugin inside it
# Usage: powershell -ExecutionPolicy Bypass -File tools\package.ps1
# Releases are built by GitHub Actions (.github\workflows\release.yml) from a version tag and signed through SignPath.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\VoidCrewModLauncher\VoidCrewModLauncher.csproj"
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$plugin = Select-String -Path (Join-Path $root "src\VoidCrewModLauncher.Bridge\Plugin.cs") -Pattern 'const string Version = "([^"]+)"'
if ($plugin.Matches[0].Groups[1].Value -ne $version) {
    throw "The bridge's Plugin.cs says $($plugin.Matches[0].Groups[1].Value) but Directory.Build.props says $version; make them match."
}

$properties = @("-p:Configuration=Release")

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

Remove-Item $publish -Recurse -Force

Write-Output ("Packed {0} ({1:N1} MB), version {2}" -f $setup, ((Get-Item $setup).Length / 1MB), $version)
