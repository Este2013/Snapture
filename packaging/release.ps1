<#
.SYNOPSIS
  Build a Snapture release: publish, package with Inno Setup, and stage it on the
  gh-pages branch (installer + releases.json). You push gh-pages afterwards.

.EXAMPLE
  ./packaging/release.ps1 -Version 1.1.0 -Notes "- Snapshot mode`n- Update checks"

.EXAMPLE
  ./packaging/release.ps1 -Version 1.1.0 -NotesFile notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [string]$NotesFile = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    # Must match UpdateService.ManifestUrl's directory.
    [string]$PagesBaseUrl = "https://este2013.github.io/TOOLS/snapture"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $PSScriptRoot "publish"
$distDir = Join-Path $PSScriptRoot "dist"
$worktree = Join-Path $PSScriptRoot ".ghpages"

if ($NotesFile) { $Notes = Get-Content -Raw $NotesFile }

function Find-Iscc {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php"
}

# 1) Publish (self-contained so users need no .NET runtime).
Write-Host "==> Publishing $Version ($Configuration/$Runtime)..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish (Join-Path $repo "src\Snapture.App\Snapture.App.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    "/p:Version=$Version" "/p:PublishSingleFile=false" -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# 2) Build the installer.
Write-Host "==> Building installer..." -ForegroundColor Cyan
$iscc = Find-Iscc
if (Test-Path $distDir) { Remove-Item -Recurse -Force $distDir }
& $iscc "/DMyAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$distDir" (Join-Path $PSScriptRoot "Snapture.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup build failed." }

$installer = Join-Path $distDir "Snapture-Setup-$Version.exe"
if (-not (Test-Path $installer)) { throw "Installer not found at $installer" }
$sha = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLower()
Write-Host "    installer: $installer" -ForegroundColor DarkGray
Write-Host "    sha256:    $sha" -ForegroundColor DarkGray

# 3) Ensure a gh-pages worktree.
Write-Host "==> Staging on gh-pages..." -ForegroundColor Cyan
if (-not (Test-Path $worktree)) {
    if (git -C $repo branch --list gh-pages) {
        git -C $repo worktree add $worktree gh-pages
    } elseif (git -C $repo ls-remote --exit-code --heads origin gh-pages 2>$null) {
        git -C $repo worktree add $worktree -b gh-pages origin/gh-pages
    } else {
        git -C $repo worktree add --detach $worktree HEAD
        Push-Location $worktree
        git switch --orphan gh-pages
        Get-ChildItem -Force | Where-Object { $_.Name -ne ".git" } | Remove-Item -Recurse -Force
        Pop-Location
    }
}

# 4) Copy the installer and update the manifest.
$snapDir = Join-Path $worktree "snapture"
New-Item -ItemType Directory -Force -Path $snapDir | Out-Null
Set-Content -Path (Join-Path $worktree ".nojekyll") -Value "" -NoNewline
Copy-Item $installer $snapDir -Force

$manifestPath = Join-Path $snapDir "releases.json"
if (Test-Path $manifestPath) {
    $manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
} else {
    $manifest = [pscustomobject]@{ product = "Snapture"; releases = @() }
}

$entry = [pscustomobject]@{
    version      = $Version
    date         = (Get-Date -Format "yyyy-MM-dd")
    notes        = $Notes
    installerUrl = "$PagesBaseUrl/Snapture-Setup-$Version.exe"
    sha256       = $sha
}

$others = @($manifest.releases | Where-Object { $_.version -ne $Version })
$manifest.releases = @($entry) + $others
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8

# 5) Commit on gh-pages (you push).
git -C $worktree add -A
git -C $worktree commit -m "release: Snapture $Version" | Out-Null

Write-Host ""
Write-Host "Done. Staged on gh-pages:" -ForegroundColor Green
Write-Host "  $manifestPath"
Write-Host "  $(Join-Path $snapDir "Snapture-Setup-$Version.exe")"
Write-Host ""
Write-Host "Next:" -ForegroundColor Yellow
Write-Host "  git push origin gh-pages"
Write-Host "  (and tag the release on main:  git tag v$Version; git push --tags)"
