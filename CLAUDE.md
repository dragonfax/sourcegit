# Personal fork of SourceGit — notes for new sessions

This repository (`dragonfax/sourcegit`) is a personal fork of
[sourcegit-scm/sourcegit](https://github.com/sourcegit-scm/sourcegit), a
cross-platform Git GUI written in C# / Avalonia. The owner runs it on **macOS**.

This file only exists on the `personal` branch. Do not copy it into feature
branches meant to be offered upstream.

## Branch layout

| Branch | Purpose |
|---|---|
| `master` | Untouched copy of upstream `master`. Only ever fast-forwarded from upstream; never commit to it. |
| `personal` | **The branch the owner builds and runs.** Upstream `master` (latest release) + all personal patches + this file. Rebased onto upstream periodically (force-push is expected and fine; only the owner uses it). |
| `feature/<name>` | One branch per personal feature, each containing only that feature's commit(s) on top of upstream, so it can be proposed upstream on its own. |

Remotes: `origin` = this fork, `upstream` = sourcegit-scm/sourcegit. A fresh
clone will not have `upstream`; add it:

```bash
git remote add upstream https://github.com/sourcegit-scm/sourcegit.git
git fetch upstream master
```

Upstream develops on `develop` and merges into `master` at each release.
`personal` is deliberately based on `upstream/master` so it only picks up
released code (`develop` may not be release-ready). Note `master` only moves
when upstream cuts a release.

### Updating from upstream

```bash
git fetch upstream master
git push origin upstream/master:master   # keep the fork's master mirrored
git checkout personal
git rebase upstream/master
git push --force-with-lease origin personal
```

### Adding a new personal feature

1. Branch from upstream: `git checkout -b feature/<name> upstream/master`.
2. Implement and commit there; push the feature branch.
3. Cherry-pick the commit(s) onto `personal`, **below** the commit that adds
   this file if convenient (order doesn't matter much), and update the
   "Personal patches" list below.
4. If upstream later accepts the feature, drop the commit from `personal` on
   the next rebase (git usually drops it automatically when identical).

## Personal patches on `personal` (oldest first)

1. **Multi-window support (macOS)** — `feat: multi-window support (macOS)`.
   Drag a repo tab out of the window to open it in a new window, or onto
   another window's tab bar to move it there. All windows share one process.
   Main files: `src/ViewModels/Launcher.cs`, `src/Views/Launcher.axaml.cs`,
   `src/Views/LauncherTabBar.axaml.cs`, `src/App.axaml.cs`,
   `src/Native/MacOS.cs`. Same commit content as `feature/multi-window`. Cannot be tested on Linux; only verified on the owner's Mac.

2. **Compare a range of selected commits** — `feat: compare commits when a
   range (2+) is selected in history`. Branch: `feature/compare-selected-commits`.
   Upstream showed the compare view only when exactly 2 commits were selected
   and a "N commits selected" count for 3+. Now any multi-selection (Cmd/Ctrl+click
   or Shift+click range) shows `RevisionCompare` between the oldest and newest
   selected commits by list position, i.e. `git diff <oldest> <newest>`
   (the oldest commit's own changes are *not* included). Direction is always
   older → newer. Change is in `PostSelectedCommitsChanged` /
   `GetSelectionEndpoints` in `src/ViewModels/Histories.cs`. The `m:Count`
   DataTemplate in `src/Views/Histories.axaml` is now unused but left in place
   to keep the diff against upstream small.
   Possible follow-up the owner may want: include the oldest commit's changes
   (diff from its first parent), like a squash view.

## Building

- Requires the **.NET 10** SDK/runtime (`global.json`) and the
  `depends/AvaloniaEdit` submodule: `git submodule update --init`.
- Run: `dotnet run --project src/SourceGit.csproj -c Release -- /path/to/repo`
- The owner's Mac has only .NET 11 installed (Homebrew). Either run with
  `DOTNET_ROLL_FORWARD=Major` or install the .NET 10 runtime alongside it.

### In a Claude Code cloud sandbox (Linux)

- `dotnet` is not preinstalled: `apt-get update && apt-get install -y dotnet-sdk-10.0`
  (the `dot.net` install script is blocked by the proxy).
- The AvaloniaEdit submodule references a NuGet feed (`nuget-feed-all.avaloniaui.net`)
  that the proxy blocks. Build with:
  ```bash
  dotnet restore src/SourceGit.csproj --ignore-failed-sources -p:NuGetAudit=false
  dotnet build src/SourceGit.csproj --no-restore -p:NuGetAudit=false
  ```
- The GUI can be exercised headlessly: `apt-get install -y xvfb xdotool imagemagick`,
  start `Xvfb :99 -screen 0 1600x1000x24 &`, run
  `DISPLAY=:99 src/bin/Debug/net10.0/SourceGit /path/to/repo &`, drive it with
  `xdotool` (e.g. `xdotool mousemove X Y keydown shift click 1 keyup shift`) and
  capture with `DISPLAY=:99 import -window root shot.png`.
  macOS-only behaviour (multi-window) can't be verified this way.
