# Contributing

Developer Control Tower is a focused Windows desktop execution console. Changes
should reduce cognitive load, improve trust in repository state, or shorten the
path into work.

## Development

Requirements:

- Windows 10 or 11;
- the .NET SDK selected by `global.json`;
- Git;
- PowerShell 7 recommended.

```powershell
dotnet restore DeveloperControlTower.sln --locked-mode
dotnet build DeveloperControlTower.sln -c Release --no-restore
dotnet test DeveloperControlTower.sln -c Release --no-restore
```

To prove the x64 and ARM64 release packages locally:

```powershell
.\Build-ReleasePackages.ps1
```

## Pull requests

- Keep changes small and complete.
- Add or update tests for behavior changes.
- Do not commit portfolio data, logs, credentials, private assets, or
  machine-specific paths.
- Do not add background services, planning dashboards, sprint tracking,
  collaboration features, or hidden synchronization.
- Pin every GitHub Action to a full immutable commit SHA.
- Never use `pull_request_target`.
- Explain user-visible behavior and security implications in the PR.

All contributions are provided under the repository's MIT licence.
