# Builds everything and runs the offline tests (no game launch): the bridge, the core, the launcher, the unit tests,
# and, when Void Crew is installed, a check that every game method the bridge hooks still exists in that version.
# Usage: powershell -ExecutionPolicy Bypass -File tools\build-and-test.ps1 [-VoidCrewDir "<game folder>"]
param(
    [string]$VoidCrewDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# The bridge first: the solution build reaches it twice (directly and through the core, which carries it), and two
# compiles of it at once fight over its output file.
dotnet build (Join-Path $root "src\VoidCrewModLauncher.Bridge\VoidCrewModLauncher.Bridge.csproj") -c Release -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "The bridge plugin didn't build." }
dotnet build (Join-Path $root "VoidCrewModLauncher.slnx") -c Release -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "The build failed." }

dotnet test (Join-Path $root "tests\VoidCrewModLauncher.Tests\VoidCrewModLauncher.Tests.csproj") -c Release --no-build -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

# The bridge only hooks a handful of the game's methods; make sure they're all still there in this game version.
# A folder typed as "...\Void Crew\" arrives with a quote on the end (Windows reads \" as an escaped quote).
$VoidCrewDir = $VoidCrewDir.Trim().TrimEnd('"')
if (-not $VoidCrewDir) { $VoidCrewDir = "G:\SteamLibrary\steamapps\common\Void Crew" }
# Path.Combine, not Join-Path: Join-Path fails outright when the drive isn't there (GitHub's machines have no G:).
$assembly = [System.IO.Path]::Combine($VoidCrewDir, "Void Crew_Data\Managed\Assembly-CSharp.dll")
if (-not (Test-Path $assembly)) {
    # GitHub's release build has no game: the bridge builds against reference packages, and the hooks are checked on
    # a PC with Void Crew before each release.
    Write-Output "Void Crew isn't installed at $VoidCrewDir; skipped the game hook checks."
    Write-Output "All builds and tests passed."
    return
}
$checks = @(
    @("PhotonService", "OnCreatedRoom"), @("PhotonService", "OnCreateRoomFailed"), @("PhotonService", "OnJoinedRoom"),
    @("PhotonService", "OnJoinRoomFailed"), @("PhotonService", "OnMasterClientSwitched"), @("PhotonService", "ConnectToRegion"),
    @("PhotonService", "ConnectingToRegion"), @("PhotonService", "CurrentRegion"),
    @("CG.GameLoopStateMachine.GameStates.GSPhotonDisconnected", "OnEnter"),
    @("CG.GameLoopStateMachine.GameStates.GSJoinExternalGame", "SetPhotonRoomToJoin"),
    @("CG.GameLoopStateMachine.GameStates.GSJoinExternalGame", "SetRegionToJoin"),
    @("MatchMakingMenu", "OnStartPublicGame"), @("MatchMakingMenu", "OnStartPrivateGame"),
    @("MatchmakingController", "GetCachedRoomList")
)
$fields = @(@("MatchMakingMenu", "_roomNameField"), @("MatchMakingMenu", "_playerLimit"), @("MatchmakingController", "_cachedMatchmakingRooms"))
# In ResourceAssets.dll: the game's own mod check, which the bridge reads to list the mods joining players need.
$resourceAssets = [System.IO.Path]::Combine($VoidCrewDir, "Void Crew_Data\Managed\ResourceAssets.dll")
$assetChecks = @(
    @("ResourceAssets.RuntimeAssetsRegister", "RegisterAsset"), @("ResourceAssets.RuntimeAssetsRegister", "RegisterExternalMod"),
    @("ResourceAssets.RuntimeAssetsRegister", "AddAssetEffect"), @("ResourceAssets.RuntimeAssetsRegister", "GetRequiredAssets"),
    @("ResourceAssets.RuntimeAssetsRegister", "GetRequiredExternalMods"), @("ResourceAssets.RuntimeAssetsRegister", "GetAsset"),
    @("ResourceAssets.RuntimeAssetsRegister", "TryGetAssetInfo"), @("ResourceAssets.RuntimeAssetsRegister", "TryGetModInfo")
)
$script = @"
using System; using System.Linq; using System.Reflection; using System.Reflection.Metadata; using System.Reflection.PortableExecutable; using System.IO;
var reader = new PEReader(File.OpenRead(args[0])).GetMetadataReader();
var types = reader.TypeDefinitions.Select(h => reader.GetTypeDefinition(h)).ToList();
string Full(TypeDefinition t) => (reader.GetString(t.Namespace) is var ns && ns.Length > 0 ? ns + "." : "") + reader.GetString(t.Name);
int missing = 0;
for (int i = 1; i < args.Length; i += 3)
{
    // A type that was renamed or removed counts its members as missing (instead of stopping the check).
    var type = types.Where(t => Full(t) == args[i]).ToList();
    bool found = type.Count > 0 && (args[i + 2] == "m"
        ? type[0].GetMethods().Any(m => reader.GetString(reader.GetMethodDefinition(m).Name) == args[i + 1])
        : type[0].GetFields().Any(f => reader.GetString(reader.GetFieldDefinition(f).Name) == args[i + 1]));
    Console.WriteLine((found ? "ok      " : type.Count == 0 ? "NO TYPE " : "MISSING ") + args[i] + "." + args[i + 1]);
    if (!found) missing++;
}
Console.WriteLine("checked " + (args.Length / 3) + ", missing " + missing);
return missing;
"@
# A folder of its own for each run (two runs at once don't share one), deleted afterwards.
$check = Join-Path ([System.IO.Path]::GetTempPath()) ("vcml-hook-check-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $check | Out-Null
try {
    Set-Content (Join-Path $check "Program.cs") $script -Encoding utf8
    Set-Content (Join-Path $check "check.csproj") '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>' -Encoding utf8
    # Built on its own first: a checker that doesn't build (a path too long for MSBuild, say) isn't missing game methods.
    dotnet build (Join-Path $check "check.csproj") -nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw "The game hook checker didn't build (see above), so the game's methods weren't checked." }

    # Runs the checker on one game DLL. It returns how many members are missing, and says so on its last line; without
    # that line it stopped before the end, whatever its exit code.
    function Test-GameMembers([string]$dll, $methods, $fieldList) {
        $arguments = @($dll)
        foreach ($c in $methods) { $arguments += $c[0], $c[1], "m" }
        foreach ($f in $fieldList) { $arguments += $f[0], $f[1], "f" }
        $output = @(dotnet run --project (Join-Path $check "check.csproj") --no-build -v:quiet -- @arguments)
        $missing = $LASTEXITCODE
        $output | Write-Output
        if (-not ($output -match '^checked \d+, missing \d+$')) { throw "The game hook check stopped before the end (exit code $missing; see above)." }
        if ($missing -ne 0) { throw "$missing game method(s) the bridge uses are gone in this Void Crew version." }
    }
    Test-GameMembers $assembly $checks $fields
    Test-GameMembers $resourceAssets $assetChecks @()
}
finally {
    Remove-Item $check -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output "All builds, tests and game checks passed."
