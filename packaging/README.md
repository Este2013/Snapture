# Packaging & releases

Snapture ships as an **Inno Setup** installer. Releases are hosted on the repo's
**`gh-pages`** branch, and the app checks a static **`releases.json`** manifest
there to offer in-app updates.

## Layout on gh-pages

```
/.nojekyll
/releases.json
/Snapture-Setup-<version>.exe
```

Served at `https://este2013.github.io/Snapture/…`, which is what
`UpdateService.ManifestUrl` points to. If the Pages URL changes (e.g. the repo is
renamed), update that constant and the `-PagesBaseUrl` default in `release.ps1`.

### `releases.json`

```json
{
  "product": "Snapture",
  "releases": [
    {
      "version": "1.1.0",
      "date": "2026-07-01",
      "notes": "- Snapshot mode\n- In-app update checks",
      "installerUrl": "https://este2013.github.io/Snapture/Snapture-Setup-1.1.0.exe",
      "sha256": "<lowercase hex>"
    }
  ]
}
```

The app picks the highest `version` greater than the running one; `sha256` is
verified after download.

## Cutting a release

Prerequisites: the .NET 9 SDK and [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
./packaging/release.ps1 -Version 1.1.0 -Notes "- Snapshot mode`n- Update checks"
# or: -NotesFile path\to\notes.md
```

This publishes a self-contained build, builds `Snapture-Setup-1.1.0.exe`, then
stages the installer + updated `releases.json` on a `gh-pages` worktree and
commits. Finish with:

```powershell
git push origin gh-pages
git tag v1.1.0 ; git push --tags
```

One-time: enable **GitHub Pages** for the repo from the **`gh-pages`** branch
(root) in the repo settings.

## How updates install

The in-app updater downloads the installer to `%TEMP%`, verifies its SHA-256,
launches it with `/SILENT`, and exits Snapture so files can be replaced. The
installer is a **per-user** install (`{localappdata}\Programs\Snapture`, no UAC),
closes any running instance, and relaunches Snapture when done.
