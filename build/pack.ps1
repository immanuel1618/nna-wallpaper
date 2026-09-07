#requires -Version 5.1
<#
.SYNOPSIS
    Build and package NNA Wallpaper into Velopack release artifacts (Setup.exe / Portable.zip / feed).

.DESCRIPTION
    Steps:
      1. Ensure the "vpk" global dotnet tool is installed at the pinned version (1.2.0).
      2. dotnet publish the WPF app as a self-contained win-x64 build into <repo>\publish.
      3. vpk pack the published output into <repo>\<OutDir> (default: Releases).
      4. Verify the expected Velopack output files are present.

    This script never publishes anything remotely (no "vpk upload"). It only produces local
    artifacts under -OutDir. Intended to be called from build/pack.ps1 locally and from
    .github/workflows/release.yml on windows-latest runners.

.PARAMETER Version
    Package version to stamp on the publish output and the Velopack package.
    Defaults to the <Version> value read from Directory.Build.props at the repo root.

.PARAMETER Configuration
    Build configuration passed to dotnet publish. Default: Release.

.PARAMETER SkipPublish
    Skip the "dotnet publish" step and reuse whatever is already in the publish folder.
    Useful for iterating on packaging only.

.PARAMETER OutDir
    Output directory (relative to the repo root) where vpk writes release artifacts.
    Default: Releases.
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [string]$OutDir = "Releases"
)

$ErrorActionPreference = "Stop"

# Resolve paths relative to the repo root (this script lives in <repo>\build).
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$ProjectPath = Join-Path $RepoRoot "src\NNA.Wallpaper\NNA.Wallpaper.csproj"
$PublishDir = Join-Path $RepoRoot "publish"
$OutputDir = Join-Path $RepoRoot $OutDir
$MainExe = "NNA.Wallpaper.exe"
$IconPath = Join-Path $RepoRoot "assets\app.ico"
$PackId = "NNA.Wallpaper"
$PackTitle = "NNA Wallpaper"
$PackAuthors = "NNA1618"
$PinnedVpkVersion = "1.2.0"

function Get-VersionFromProps {
    $propsPath = Join-Path $RepoRoot "Directory.Build.props"
    if (-not (Test-Path $propsPath)) {
        throw "Cannot resolve -Version: Directory.Build.props not found at $propsPath"
    }
    $content = Get-Content -Raw -Path $propsPath
    $match = [regex]::Match($content, "<Version>([^<]+)</Version>")
    if (-not $match.Success) {
        throw "Cannot resolve -Version: no <Version> element found in Directory.Build.props"
    }
    return $match.Groups[1].Value.Trim()
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-VersionFromProps
    Write-Host "No -Version supplied; using $Version from Directory.Build.props"
}

Write-Host "=== NNA Wallpaper pack ==="
Write-Host "Repo root     : $RepoRoot"
Write-Host "Version       : $Version"
Write-Host "Configuration : $Configuration"
Write-Host "Publish dir   : $PublishDir"
Write-Host "Output dir    : $OutputDir"

# --- Step (a): ensure vpk 1.2.0 is installed as a global tool ---------------------------------
Write-Host "`n--- Checking vpk global tool ---"
$toolList = dotnet tool list -g
$vpkLine = $toolList | Select-String -Pattern "^\s*vpk\s"

if (-not $vpkLine) {
    Write-Host "vpk not found; installing version $PinnedVpkVersion"
    dotnet tool install -g vpk --version $PinnedVpkVersion
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool install -g vpk failed with exit code $LASTEXITCODE" }
}
else {
    $installedVersion = ($vpkLine -split "\s+")[1]
    if ($installedVersion -ne $PinnedVpkVersion) {
        Write-Host "vpk $installedVersion found; updating to pinned version $PinnedVpkVersion"
        dotnet tool update -g vpk --version $PinnedVpkVersion
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool update -g vpk failed with exit code $LASTEXITCODE" }
    }
    else {
        Write-Host "vpk $PinnedVpkVersion already installed"
    }
}

# --- Step (b): dotnet publish -------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host "`n--- dotnet publish ---"
    if (Test-Path $PublishDir) {
        Remove-Item -Recurse -Force $PublishDir
    }

    dotnet publish $ProjectPath `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -o $PublishDir

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
else {
    Write-Host "`n--- Skipping dotnet publish (-SkipPublish) ---"
}

# Verify publish output looks sane before handing it to vpk.
$exePath = Join-Path $PublishDir $MainExe
if (-not (Test-Path $exePath)) {
    Write-Error "Publish output is missing $MainExe at $exePath"
    exit 1
}

$requiredContentDirs = @("wallpaper", "widgets", "settings")
foreach ($dir in $requiredContentDirs) {
    $dirPath = Join-Path $PublishDir $dir
    if (-not (Test-Path $dirPath)) {
        Write-Error "Publish output is missing expected content folder: $dir (looked in $dirPath)"
        exit 1
    }
}
Write-Host "Publish output verified: $MainExe plus wallpaper/widgets/settings present."

# --- Step (c): vpk pack -----------------------------------------------------------------------
Write-Host "`n--- vpk pack ---"
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$vpkArgs = @(
    "pack",
    "--packId", $PackId,
    "--packVersion", $Version,
    "--packDir", $PublishDir,
    "--mainExe", $MainExe,
    "--packTitle", $PackTitle,
    "--packAuthors", $PackAuthors,
    "--outputDir", $OutputDir
)

if (Test-Path $IconPath) {
    $vpkArgs += @("--icon", $IconPath)
}
else {
    Write-Warning "assets/app.ico not found at $IconPath; packing without --icon."
}

& vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }

# --- Step (d): verify expected release artifacts --------------------------------------------
Write-Host "`n--- Verifying release artifacts in $OutputDir ---"
$releaseFiles = Get-ChildItem -Path $OutputDir -File -ErrorAction SilentlyContinue

if (-not $releaseFiles -or $releaseFiles.Count -eq 0) {
    Write-Error "No files found in output directory $OutputDir"
    exit 1
}

$releaseFiles | Sort-Object Name | ForEach-Object {
    "{0,12:N0} bytes  {1}" -f $_.Length, $_.Name
} | Write-Host

$hasSetup = $releaseFiles | Where-Object { $_.Name -like "*Setup.exe" }
$hasPortable = $releaseFiles | Where-Object { $_.Name -like "*Portable.zip" }
$hasFeedJson = $releaseFiles | Where-Object { $_.Name -like "releases*.json" }

if (-not $hasSetup) {
    Write-Error "Missing expected artifact matching *Setup.exe in $OutputDir"
    exit 1
}
if (-not $hasPortable) {
    Write-Error "Missing expected artifact matching *Portable.zip in $OutputDir"
    exit 1
}
if (-not $hasFeedJson) {
    Write-Error "Missing expected feed file matching releases*.json in $OutputDir"
    exit 1
}

Write-Host "`nAll expected artifacts present: *Setup.exe, *Portable.zip, releases*.json."
Write-Host "Pack complete: $OutputDir"
exit 0
