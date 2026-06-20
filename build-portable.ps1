#requires -Version 5.1
<#
.SYNOPSIS
    Builds SrcWorkshop and publishes a portable, ready-to-run drop into .\publish.

.DESCRIPTION
    Produces:
        publish\SrcWorkshop.exe              - the WPF UI (run this)
        publish\SrcWorkshop.SteamHost.exe    - per-game Steam host (launched by the App)
        + SrcWorkshop.SteamBridge.dll, steam_api64.dll, Ijwhost.dll and app DLLs

    The builds are framework-dependent (no bundled .NET runtime, no single-file), so the
    target machine needs the .NET 10 Desktop Runtime (x64). See README.md.

    Both projects are published into the SAME folder because the App locates the host by the
    filename SrcWorkshop.SteamHost.exe sitting next to it.

    The whole solution must be built with VS MSBuild, not the dotnet CLI: the C++/CLI
    SteamBridge needs MSBuild (the dotnet CLI cannot evaluate $(VCTargetsPath)).

.NOTES
    Requires Visual Studio 2026 with the .NET 10 SDK and the C++/CLI component, and the
    Steamworks SDK placed under external\steamworks_sdk\ (see external\steamworks_sdk\PLACEMENT.md).

.EXAMPLE
    .\build-portable.ps1
    .\build-portable.ps1 -Configuration Release -OutDir C:\drops\SrcWorkshop
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

$sln      = Join-Path $PSScriptRoot 'SrcWorkshop.sln'
$appProj  = Join-Path $PSScriptRoot 'src\SrcWorkshop.App\SrcWorkshop.App.csproj'
$hostProj = Join-Path $PSScriptRoot 'src\SrcWorkshop.SteamHost\SrcWorkshop.SteamHost.csproj'

# --- Clean output ------------------------------------------------------------
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

$common = @("/p:Configuration=$Configuration", "/p:Platform=$Platform", '/v:minimal', '/nologo')

# 1) Build the full solution so the C++/CLI bridge DLL exists (Publish can't build it).
Write-Host "`n=== Building solution ($Configuration|$Platform) ===" -ForegroundColor Yellow
& $msbuild $sln /t:Restore @common
if ($LASTEXITCODE -ne 0) { Fail "Restore failed ($LASTEXITCODE)." }
& $msbuild $sln /t:Build @common
if ($LASTEXITCODE -ne 0) { Fail "Solution build failed ($LASTEXITCODE)." }

# 2) Publish the host first, then the App, both into the shared OutDir via their Folder profiles.
Write-Host "`n=== Publishing SteamHost ===" -ForegroundColor Yellow
& $msbuild $hostProj /t:Publish /p:PublishProfile=FolderProfile "/p:PublishDir=$OutDir\" @common
if ($LASTEXITCODE -ne 0) { Fail "SteamHost publish failed ($LASTEXITCODE)." }

Write-Host "`n=== Publishing App ===" -ForegroundColor Yellow
& $msbuild $appProj /t:Publish /p:PublishProfile=FolderProfile "/p:PublishDir=$OutDir\" @common
if ($LASTEXITCODE -ne 0) { Fail "App publish failed ($LASTEXITCODE)." }

# 3) Trim debug symbols left by the build (the native bridge .pdb in particular).
Get-ChildItem $OutDir -Include *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

# --- Sanity-check the drop has everything it needs ---------------------------
$required = 'SrcWorkshop.exe', 'SrcWorkshop.SteamHost.exe', 'SrcWorkshop.SteamBridge.dll',
           'steam_api64.dll', 'Ijwhost.dll'
$missing = $required | Where-Object { -not (Test-Path (Join-Path $OutDir $_)) }
if ($missing) { Fail "Publish is missing required files: $($missing -join ', ')" }

# --- Summary -----------------------------------------------------------------
Write-Host "`n=== Portable build ready: $OutDir ===" -ForegroundColor Green
Write-Host "Run: $(Join-Path $OutDir 'SrcWorkshop.exe')  (needs .NET 10 Desktop Runtime + Steam)" -ForegroundColor Green
Get-ChildItem $OutDir -File | Sort-Object Name |
    Format-Table Name, @{N='KB';E={[math]::Round($_.Length/1KB,1)}} -AutoSize
