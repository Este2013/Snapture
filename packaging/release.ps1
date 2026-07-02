<#
.SYNOPSIS
  Build and publish a Snapture release:
    1. publish (self-contained) + package with Inno Setup,
    2. upload the installer as a GitHub *Release* asset (reliable binary hosting),
    3. update releases.json on the gh-pages branch to point at that asset, and push.

  The update feed is therefore: releases.json (served from gh-pages via
  raw.githubusercontent, no Pages build needed) -> GitHub Release assets.

  Authenticates to the GitHub API with your existing git credential
  (git credential manager), so no `gh` CLI or manual token is required.

.EXAMPLE
  ./packaging/release.ps1 -Version 1.3.0 -Notes "- Faster capture`n- Bug fixes"

.EXAMPLE
  ./packaging/release.ps1 -Version 1.3.0 -NotesFile notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [string]$NotesFile = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Repo = "Este2013/Snapture",
    # Commit/branch the release tag should point at.
    [string]$Target = "main",
    # Set to skip the automatic `git push origin gh-pages` at the end.
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
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

function Get-GitHubToken {
    # Reuse whatever credential git already uses to push to github.com.
    $out = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    $line = $out | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if (-not $line) { throw "Could not resolve a GitHub token from git credential manager. Run 'git push' once to sign in, then retry." }
    return $line.Substring('password='.Length)
}

# 1) Publish (self-contained so users need no .NET runtime).
Write-Host "==> Publishing $Version ($Configuration/$Runtime)..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish (Join-Path $repoRoot "src\Snapture.App\Snapture.App.csproj") `
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
$assetName = "Snapture-Setup-$Version.exe"
$sha = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLower()
Write-Host "    installer: $installer" -ForegroundColor DarkGray
Write-Host "    sha256:    $sha" -ForegroundColor DarkGray

# 3) Create (or reuse) the GitHub Release for tag v$Version and upload the installer.
Write-Host "==> Publishing GitHub Release v$Version..." -ForegroundColor Cyan
$tok = Get-GitHubToken
$headers = @{ Authorization = "Bearer $tok"; Accept = "application/vnd.github+json"; "User-Agent" = "snapture-release" }
$api = "https://api.github.com/repos/$Repo"
$uploads = "https://uploads.github.com/repos/$Repo"
$tag = "v$Version"

$release = $null
try { $release = Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $headers -Method Get } catch { }
if (-not $release) {
    $body = @{ tag_name = $tag; target_commitish = $Target; name = "Snapture $Version"; body = $Notes; draft = $false; prerelease = $false } | ConvertTo-Json
    $release = Invoke-RestMethod -Uri "$api/releases" -Headers $headers -Method Post -Body $body -ContentType "application/json"
    Write-Host "    created release $tag (id $($release.id))" -ForegroundColor DarkGray
} else {
    Write-Host "    reusing existing release $tag (id $($release.id))" -ForegroundColor DarkGray
    # Replace an existing same-named asset so re-runs are idempotent.
    $existing = $release.assets | Where-Object { $_.name -eq $assetName }
    foreach ($a in $existing) { Invoke-RestMethod -Uri "$api/releases/assets/$($a.id)" -Headers $headers -Method Delete | Out-Null }
}

$asset = Invoke-RestMethod -Uri "$uploads/releases/$($release.id)/assets?name=$assetName" `
    -Headers $headers -Method Post -InFile $installer -ContentType "application/octet-stream"
$installerUrl = $asset.browser_download_url
if (-not $installerUrl) { throw "Asset upload did not return a download URL." }
Write-Host "    asset: $installerUrl" -ForegroundColor DarkGray

# 4) Ensure a gh-pages worktree (manifest only — no binaries live here anymore).
Write-Host "==> Updating releases.json on gh-pages..." -ForegroundColor Cyan
if (-not (Test-Path $worktree)) {
    if (git -C $repoRoot branch --list gh-pages) {
        git -C $repoRoot worktree add $worktree gh-pages
    } elseif (git -C $repoRoot ls-remote --exit-code --heads origin gh-pages 2>$null) {
        git -C $repoRoot worktree add $worktree -b gh-pages origin/gh-pages
    } else {
        git -C $repoRoot worktree add --detach $worktree HEAD
        Push-Location $worktree
        git switch --orphan gh-pages
        Get-ChildItem -Force | Where-Object { $_.Name -ne ".git" } | Remove-Item -Recurse -Force
        Pop-Location
    }
}
# Keep the manifest branch lean: drop any installers a previous scheme left behind.
Get-ChildItem -Path $worktree -Filter "*.exe" -File -ErrorAction SilentlyContinue | ForEach-Object {
    git -C $worktree rm --quiet --ignore-unmatch $_.Name | Out-Null
    Remove-Item -Force $_.FullName -ErrorAction SilentlyContinue
}
Set-Content -Path (Join-Path $worktree ".nojekyll") -Value "" -NoNewline

$manifestPath = Join-Path $worktree "releases.json"
if (Test-Path $manifestPath) {
    $manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
} else {
    $manifest = [pscustomobject]@{ product = "Snapture"; releases = @() }
}

$entry = [pscustomobject]@{
    version      = $Version
    date         = (Get-Date -Format "yyyy-MM-dd")
    notes        = $Notes
    installerUrl = $installerUrl
    sha256       = $sha
}
$others = @($manifest.releases | Where-Object { $_.version -ne $Version })
$manifest.releases = @($entry) + $others
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8

# 5) Commit and push gh-pages (tiny, so it publishes to raw immediately).
git -C $worktree add -A
git -C $worktree commit -m "release: Snapture $Version" | Out-Null
if ($NoPush) {
    Write-Host ""
    Write-Host "Staged on gh-pages (not pushed). Finish with:" -ForegroundColor Yellow
    Write-Host "  git push origin gh-pages"
} else {
    git -C $worktree push origin gh-pages
    Write-Host ""
    Write-Host "Done — Snapture $Version is live." -ForegroundColor Green
    Write-Host "  installer: $installerUrl"
    Write-Host "  manifest:  https://raw.githubusercontent.com/$Repo/gh-pages/releases.json"
}
