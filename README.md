# Developer Control Tower

[![CI](https://github.com/u64a/developer-control-tower/actions/workflows/ci.yml/badge.svg)](https://github.com/u64a/developer-control-tower/actions/workflows/ci.yml)
[![CodeQL](https://github.com/u64a/developer-control-tower/actions/workflows/codeql.yml/badge.svg)](https://github.com/u64a/developer-control-tower/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/u64a/developer-control-tower/badge)](https://scorecard.dev/viewer/?uri=github.com/u64a/developer-control-tower)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)

Developer Control Tower is a lightweight, single-user Windows desktop app for
staying oriented across local, SSH, hosted-only, and hybrid Git projects. It
shows repository truth, keeps durable project context portable, and launches
the right work surface without becoming another planning system.

![Developer Control Tower showing a grouped portfolio of sample projects with repository state and per-project launch environment icons](docs/images/main-window.png)

*Sample data only: the Contoso, Fabrikam, Northwind and Tailwind projects are fictional.*

## What it does

- Presents a dense, keyboard-friendly portfolio of known projects, grouped and
  filterable by state (behind, uncommitted, clean, unknown).
- Shows branch, working-tree, upstream, availability, and recent-activity state.
- Launches local VS Code, VS Code Remote SSH, GitHub, Azure DevOps, and docs.
- Opens each project in its launch environment (see below).
- Opens a Ctrl+K launcher anywhere in the app. Search for a project, press
  Enter to open it, Ctrl+Enter to expand into the full console, or Ctrl+L to
  open the asset library.
- Supports project registration, grouping, relocation, restore, and discovery.
- Uses user-named Workspace Profiles to scope one synced portfolio per device.
- Maintains a portable reusable-asset library with explicit push and pull.
- Follows light or dark mode, with a selectable theme for each mode (including
  six original retro-console and holiday palettes, plus palettes adapted from
  Omarchy) and a selectable accent colour. The two holiday themes add a
  subtle, static decorative pattern to the window background.
- Stores SSH passwords and trusted fingerprints in Windows Credential Manager.

It does **not** provide sprint tracking, estimates, capacity planning,
collaboration, dashboards, or an always-on synchronization service.

## Launch environments

Every project opens in a launch environment. VS Code is the default. You can
also choose a terminal agent:

| Environment        | Opens                                                        |
| ------------------ | ------------------------------------------------------------ |
| VS Code            | The folder locally, or through Remote SSH for SSH projects.  |
| GitHub Copilot CLI | `copilot` in PowerShell inside Windows Terminal.             |
| Claude Code        | `claude` in PowerShell inside Windows Terminal.              |
| Custom             | Any command you define under `launch.environments` in `settings.yml`. |

Set the default under **Settings → Launch environment**. To override it for
one project, right-click that project's row and choose **Launch environment**.
Each row's launch button shows the icon of the tool that will open.

For a local project using the built-in VS Code environment, two optional
per-project checkboxes appear above the Copilot option. They can update the
official per-user stable VS Code install and/or extensions before the editor
opens. Both default off. Updates are skipped (while the project still opens)
when any VS Code, Insiders, or Code tunnel process is already present; remote,
terminal, Copilot, Claude, custom-editor, and Administrator launches never
perform this maintenance.

For SSH projects, terminal environments connect with your SSH configuration
and keys, then start the tool in the remote folder. Remote paths are
validated before they reach the remote shell. Product icons are read from the
tools installed on your machine, not bundled with the app. A generic terminal
icon appears when a tool isn't installed.

![Settings dialog showing the default launch environment selector](docs/images/settings-launch-environment.png)

## Archive safety net

Projects in the **Archive** group can keep a full, local safety-net clone of
their history and release assets. Configure its one shared storage root in
**Settings → Archive safety net**, then explicitly choose **Sync all archives
now** from an Archive project’s row menu whenever you want. The app also makes
one guarded attempt automatically each time it starts, after your portfolio
has finished loading — the same canonical inventory and whole-batch safety
preflight as the manual action, never a timer or repeat. A refusal at startup
never pops the blocking dialog (that stays reserved for the manual click); it
only shows as "Blocked"/status row text you can act on. Leaving the setting
blank uses `%USERPROFILE%\DeveloperControlTower\Archive`; it does not disable
archiving or use the profile directory itself.

For supported public HTTPS remotes, archive sync asks GitHub or a
Forgejo-compatible host for its single current/latest release and captures
every attached asset from that one release. This includes all platform and
architecture variants (and supporting files); it does not download historical
releases. GitHub SHA-256 digests are verified when supplied. Forgejo assets
without a provider checksum are retained as explicitly **Unverifiable**, never
represented as verified. A configured GitHub credential is sent only to
GitHub's own HTTPS endpoints, never to Forgejo or external asset hosts,
including after redirects.

Archive rows distinguish the source clone from release capture: **Needs sync**
means the current-root clone is absent; **Archived** means the clone and every
current-release asset are present; **No current release** is a confirmed
provider result; and **Release unsupported**, **Release incomplete**, or
**Release missing** require attention. Legacy archive state is shown as
**Release unchecked** until a sync records current-release evidence. An
**Unverifiable** release is still captured, with its missing provider checksum
called out in the detail text.
Source-refresh failures remain visible separately from earlier release errors.

Changing the root refreshes status only. It never moves, deletes, or syncs
existing archive data. A project whose old copy lives elsewhere is shown as
needing sync until you explicitly create a copy in the new global root. Legacy
`archive.path` entries in `project.yml` are ignored and reported as a warning;
they no longer select an archive destination.

The archive copy is always a dedicated clone, with its own release backup,
separate from every registered local working source. This applies equally to
forks and original upstream repositories: a matching `origin` or `upstream`
remote never lets the app update, reset, or download releases into your working
folder. Before every requested archive sync, the app reloads the complete
registered local-source inventory from the canonical portfolio and project
metadata, then physically resolves and checks the selected global root against it,
including projects outside the active profile, group, filter, or search view.
Unreadable project metadata or duplicate project IDs block the whole sync until
the registry is corrected; warning-only legacy `archive.path` entries do not.
Sync is blocked when the root equals a source folder, contains one, or sits
inside one; move the source and update its registered path, or choose a
separate archive root. Sibling folders such as `C:\archive` and
`C:\archive-work` remain valid. Existing archive-owned clones below the global
root are expected and do not count as working sources.

Configured archive roots may be any fully qualified local folder, including a
separate drive. Drive roots, the profile root, network/device paths, junctions,
mapped-network drive aliases, and the running app's install folder are
refused after their physical location is resolved. An invalid or malformed
explicit archive setting disables archive syncing until it is corrected; it
never silently falls back to the default location.

## Copilot CLI autostart

A project can start a Copilot CLI session as part of its launch, so you don't
have to open a terminal and retype the same command each time. Select the
project and use the **LAUNCH** card:

| Option    | Choices                                            | Flag             |
| --------- | -------------------------------------------------- | ---------------- |
| Autostart | Off by default; turning it on reveals the rest.     | —                |
| Session   | Resume via the picker (default), a named session, or continue the most recent. | `--resume`, `--name`, `--continue` |
| Agent     | None (default), or a custom agent name.             | `--agent`        |
| VS Code terminal | Off by default; see below.                  | —                |
| `--yolo`  | Off by default.                                     | `--yolo`         |
| Check for updates | Off by default; turning it on reveals the channel. | `update`  |
| Update channel | Stable (default) or Prerelease.                | `update stable`, `update prerelease` |

The options are saved per project in that project's `project.yml`, so they
apply again the next time you launch it. A project that has never been
configured starts nothing, exactly as before.

### Checking for updates before launching

**Check for Copilot updates before launching** downloads a pending update
first, so a session doesn't start and then immediately tell you an update is
waiting.

`update` is a subcommand rather than a flag, so it cannot share a command line
with the session options. It runs as its own command, chained ahead of the
session:

```
copilot update stable; copilot --yolo --agent squad --continue
```

On standalone installations a downloaded version applies at the next launch of
the CLI — which is the chained session command. The channel is always written
out, so the command says which channel it used rather than relying on the
CLI's own default.

The update step never receives the launch environment's own arguments, since
those are session flags the `update` subcommand would reject. A failed update
check — being offline, for example — does not stop the session from starting.

Because the update step acts on the globally installed CLI rather than on the
project, a `project.yml` found inside a working tree cannot select the
prerelease channel; it is read as stable and a warning is recorded. Choosing
prerelease stays an explicit action in the app.

Where the session appears depends on the launch environment:

- **GitHub Copilot CLI** — the flags are added to the session that already
  opens, so nothing extra is launched.
- **VS Code (or any editor environment)** — VS Code has no command line that
  opens its integrated terminal and runs a command, so Copilot CLI opens in a
  terminal beside the editor, in the same folder. If the editor opens but
  Copilot CLI cannot start, the editor launch still succeeds and the status
  line says why.
- **Another agent's CLI, such as Claude Code** — the options are hidden
  entirely. Those tools take different options, so there is nothing for
  Copilot autostart to do.
- **SSH projects launched into an editor** — no session starts, because
  Copilot CLI would run on this machine rather than the remote host.

### Running inside VS Code's terminal

VS Code has no command line that runs a command in its integrated terminal.
The only supported mechanism is a task that VS Code runs when it opens a
folder, so **Run in VS Code's terminal** generates one:

- `.vscode/tasks.json` is written before the editor starts, with a single
  `Developer Control Tower: Copilot CLI` task using `runOn: folderOpen`.
- With **Check for updates** on, a second
  `Developer Control Tower: Copilot CLI update` task is written and the
  session task `dependsOn` it with `dependsOrder: sequence`. A task takes a
  command plus an argument array rather than a shell line, so the update
  subcommand cannot be chained onto the session command and runs as its own
  task instead. Only the session task opens on `folderOpen`; the update task
  is pulled in as its dependency.
- An existing `tasks.json` is merged — only the tool's own labels are replaced,
  and a file that cannot be parsed is left untouched and reported.
- The file is added to `.git/info/exclude`, so it never reaches a commit and
  no tracked `.gitignore` is modified.
- Clearing the checkbox removes the generated tasks again.

Two limits come from VS Code itself. The task runs only when the folder opens
in a **new** window, so an already-open project just gets focus and no
session. And the first automatic task of any kind needs permission, which VS
Code records as `"task.allowAutomaticTasks": "on"` in your **user** settings —
a global switch, so from then on any folder with such a task runs it without
asking. Set it to `"auto"` if you would rather be asked per folder.

**Open Code RunAs Admin** starts the Copilot session too. The editor is
elevated but the Copilot terminal is not, because the CLI never needs
Administrator. If Developer Control Tower is itself already running elevated,
Windows raises no prompt and VS Code opens as a new window inside the elevated
instance you already have, which is easy to miss among existing windows.

Session and agent names are limited to letters, numbers, dot, underscore and
hyphen. Anything else is refused rather than placed on a command line, and a
hand-edited `project.yml` containing an unsupported name loads with a warning
and ignores that name.

## Install

Download the Setup file for your architecture from
[GitHub Releases](https://github.com/u64a/developer-control-tower/releases):

- `win-x64` for Intel and AMD 64-bit Windows;
- `win-arm64` for ARM64 Windows.

The one-click installer is per-user and installs under
`%LOCALAPPDATA%\u64a.DeveloperControlTower`. Updates stay on the architecture
channel originally installed. Release builds are self-contained, so no
separate .NET runtime is needed. Git, VS Code, Windows Terminal, and any
terminal agents you choose are used from your own installation.

> [!WARNING]
> Preview installers are not yet Authenticode-signed. Windows may show a
> SmartScreen or Defender reputation warning. Download only from this
> repository, verify the published SHA-256 file, and use
> `gh attestation verify <file> --repo u64a/developer-control-tower` when
> possible.

## Portable data

The app keeps portable configuration outside the replaceable install folder:

1. OneDrive for Business, when available;
2. personal OneDrive;
3. `%APPDATA%` as a local fallback.

The default asset library lives under that same configuration root. Machine
preferences, cache, and logs live under
`%LOCALAPPDATA%\DeveloperControlTower`. Credentials remain in Windows
Credential Manager.

Uninstalling from Windows Settings removes only the app and preserves all data.
The in-app uninstall flow can instead remove:

1. the app plus machine-local state while keeping portable data;
2. portable configuration while keeping the asset library;
3. the entire app-managed portable folder, including the default library.

Credential Manager entries are always preserved because Git and other tools
may share them.

## Build

Building requires the .NET 10 SDK pinned in [global.json](global.json).

```powershell
dotnet restore DeveloperControlTower.sln --locked-mode
dotnet build DeveloperControlTower.sln -c Release --no-restore
dotnet test DeveloperControlTower.sln -c Release --no-restore
```

Create both release channels locally:

```powershell
.\Build-ReleasePackages.ps1
```

The release script restores the pinned Velopack CLI, validates locked
dependencies, runs tests, verifies each native PE architecture, rejects the
private legacy-library boundary, and emits Setup, portable, feed, package,
SBOM-ready, and SHA-256 artifacts.

See [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), and
[docs/architecture.md](docs/architecture.md).

## Licence

MIT. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
