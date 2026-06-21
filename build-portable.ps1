#requires -Version 5.1
<#
.SYNOPSIS
    Builds PulseWorkshop and publishes a single, self-extracting exe into .\publish.

.DESCRIPTION
    Produces ONE file:
        publish\PulseWorkshop.exe   - the WPF UI; everything else is bundled inside it

    How the single file works:
      - The SteamHost is published as its own self-extracting single exe, then embedded into the App
        as a resource. At runtime HostLocator extracts it to %LocalAppData%\PulseWorkshop\host\ and
        launches it. The host, in turn, unpacks its native + C++/CLI dependencies
        (steam_api64.dll, PulseWorkshop.SteamBridge.dll, Ijwhost.dll) to a temp dir when it runs.

    The build is framework-dependent (no bundled .NET runtime), so the target machine needs the
    .NET 10 Desktop Runtime (x64). See README.md.

    The whole solution must be built with VS MSBuild, not the dotnet CLI: the C++/CLI
    SteamBridge needs MSBuild (the dotnet CLI cannot evaluate $(VCTargetsPath)).

.NOTES
    Requires Visual Studio 2026 with the .NET 10 SDK and the C++/CLI component, and the
    Steamworks SDK placed under external\steamworks_sdk\ (see external\steamworks_sdk\PLACEMENT.md).

.EXAMPLE
    .\build-portable.ps1
    .\build-portable.ps1 -Configuration Release -OutDir C:\drops\PulseWorkshop
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutDir = (Join-Path $PSScriptRoot 'publish')
)

$ErrorActionPreference = 'Stop'
$Platform = 'x64'

function Fail([string]$msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# --- Locate VS MSBuild -------------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { Fail "vswhere.exe not found at $vswhere (install Visual Studio 2026)." }
$msbuild = & $vswhere -latest -prerelease -products * `
    -requires Microsoft.Component.MSBuild `
    -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { Fail 'MSBuild.exe not found via vswhere.' }
Write-Host "MSBuild: $msbuild" -ForegroundColor Cyan

# --- Sanity: Steamworks SDK present -----------------------------------------
$steamDll = Join-Path $PSScriptRoot 'external\steamworks_sdk\redistributable_bin\win64\steam_api64.dll'
if (-not (Test-Path $steamDll)) {
    Fail "Steamworks SDK missing: $steamDll`n      See external\steamworks_sdk\PLACEMENT.md."
}

$sln      = Join-Path $PSScriptRoot 'PulseWorkshop.sln'
$appProj  = Join-Path $PSScriptRoot 'src\PulseWorkshop.App\PulseWorkshop.App.csproj'
$hostProj = Join-Path $PSScriptRoot 'src\PulseWorkshop.SteamHost\PulseWorkshop.SteamHost.csproj'

# --- Clean output ------------------------------------------------------------
$stageDir = Join-Path $PSScriptRoot '.hoststage'
if (Test-Path $OutDir)   { Remove-Item $OutDir   -Recurse -Force }
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }

$common = @("/p:Configuration=$Configuration", "/p:Platform=$Platform", '/v:minimal', '/nologo')

# 1) Build the full solution so the C++/CLI bridge DLL exists (Publish can't build it).
Write-Host "`n=== Building solution ($Configuration|$Platform) ===" -ForegroundColor Yellow
& $msbuild $sln /t:Restore @common
if ($LASTEXITCODE -ne 0) { Fail "Restore failed ($LASTEXITCODE)." }
& $msbuild $sln /t:Build @common
if ($LASTEXITCODE -ne 0) { Fail "Solution build failed ($LASTEXITCODE)." }

# 2) Publish the host as its own self-extracting single exe into the staging folder.
Write-Host "`n=== Publishing SteamHost (single-file) ===" -ForegroundColor Yellow
& $msbuild $hostProj /t:Publish /p:PublishProfile=SingleFileProfile "/p:PublishDir=$stageDir\" @common
if ($LASTEXITCODE -ne 0) { Fail "SteamHost publish failed ($LASTEXITCODE)." }

$stagedHost = Join-Path $stageDir 'PulseWorkshop.SteamHost.exe'
if (-not (Test-Path $stagedHost)) { Fail "Staged host exe not found: $stagedHost" }

# 3) Publish the App as a single exe, embedding the staged host (EmbeddedHostExe -> resource).
Write-Host "`n=== Publishing App (single-file, host embedded) ===" -ForegroundColor Yellow
& $msbuild $appProj /t:Publish /p:PublishProfile=SingleFileProfile "/p:PublishDir=$OutDir\" "/p:EmbeddedHostExe=$stagedHost" @common
if ($LASTEXITCODE -ne 0) { Fail "App publish failed ($LASTEXITCODE)." }

# 4) Trim any stray debug symbols and drop the staging folder.
Get-ChildItem $OutDir -Include *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue

# --- Sanity-check the drop has the single exe --------------------------------
$appExe = Join-Path $OutDir 'PulseWorkshop.exe'
if (-not (Test-Path $appExe)) { Fail "Publish is missing PulseWorkshop.exe." }

# --- Summary -----------------------------------------------------------------
Write-Host "`n=== Single-file build ready: $OutDir ===" -ForegroundColor Green
Write-Host "Run: $appExe  (needs .NET 10 Desktop Runtime + Steam)" -ForegroundColor Green
Get-ChildItem $OutDir -File | Sort-Object Name |
    Format-Table Name, @{N='KB';E={[math]::Round($_.Length/1KB,1)}} -AutoSize
