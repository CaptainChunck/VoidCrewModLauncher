# Builds everything and runs the offline tests (no game launch): the bridge against your installed game, the core,
# the launcher, the unit tests, and a check that every game method the bridge hooks still exists.
# Usage: powershell -ExecutionPolicy Bypass -File tools\build-and-test.ps1 [-VoidCrewDir "<game folder>"] [-BepInExDir "<BepInEx folder>"]
param(
    [string]$VoidCrewDir = "",
    [string]$BepInExDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$properties = @()
if ($VoidCrewDir) { $properties += "-p:VoidCrewDir=$VoidCrewDir" }
if ($BepInExDir) { $properties += "-p:BepInExDir=$BepInExDir" }

# The bridge first: the solution build reaches it twice (directly and through the core, which carries it), and two
# compiles of it at once fight over its output file.
dotnet build (Join-Path $root "src\VoidCrewModLauncher.Bridge\VoidCrewModLauncher.Bridge.csproj") -c Release @properties -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "The bridge plugin didn't build." }
dotnet build (Join-Path $root "VoidCrewModLauncher.slnx") -c Release @properties -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "The build failed." }

dotnet test (Join-Path $root "tests\VoidCrewModLauncher.Tests\VoidCrewModLauncher.Tests.csproj") -c Release --no-build -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

# The bridge only hooks a handful of the game's methods; make sure they're all still there in this game version.
if (-not $VoidCrewDir) { $VoidCrewDir = "G:\SteamLibrary\steamapps\common\Void Crew" }
$assembly = Join-Path $VoidCrewDir "Void Crew_Data\Managed\Assembly-CSharp.dll"
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
$resourceAssets = Join-Path $VoidCrewDir "Void Crew_Data\Managed\ResourceAssets.dll"
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
    var type = types.FirstOrDefault(t => Full(t) == args[i]);
    bool found = args[i + 2] == "m"
        ? type.GetMethods().Any(m => reader.GetString(reader.GetMethodDefinition(m).Name) == args[i + 1])
        : type.GetFields().Any(f => reader.GetString(reader.GetFieldDefinition(f).Name) == args[i + 1]);
    Console.WriteLine((found ? "ok      " : "MISSING ") + args[i] + "." + args[i + 1]);
    if (!found) missing++;
}
return missing;
"@
$check = Join-Path ([System.IO.Path]::GetTempPath()) "vcml-hook-check"
New-Item -ItemType Directory -Force $check | Out-Null
Set-Content (Join-Path $check "Program.cs") $script -Encoding utf8
Set-Content (Join-Path $check "check.csproj") '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>' -Encoding utf8
$arguments = @($assembly)
foreach ($c in $checks) { $arguments += $c[0], $c[1], "m" }
foreach ($f in $fields) { $arguments += $f[0], $f[1], "f" }
dotnet run --project (Join-Path $check "check.csproj") -v:quiet -- @arguments
if ($LASTEXITCODE -ne 0) { throw "$LASTEXITCODE game method(s) the bridge uses are gone in this Void Crew version." }
$arguments = @($resourceAssets)
foreach ($c in $assetChecks) { $arguments += $c[0], $c[1], "m" }
dotnet run --project (Join-Path $check "check.csproj") -v:quiet -- @arguments
if ($LASTEXITCODE -ne 0) { throw "$LASTEXITCODE game method(s) the bridge uses are gone in this Void Crew version." }
Write-Output "All builds, tests and game checks passed."
