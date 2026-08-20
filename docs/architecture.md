# Architecture

Developer Control Tower is a .NET 8 WPF application with four projects:

| Project | Responsibility |
|---|---|
| `ControlTower.Core` | Domain models, contracts, validation, and use cases |
| `ControlTower.Infrastructure` | Git, YAML, filesystem, SSH, credentials, library, launch, update, and uninstall adapters |
| `ControlTower.Desktop` | WPF presentation and composition |
| `ControlTower.Tests` | Unit, integration, security, and regression coverage |

## Sources of truth

- `portfolio.yml`: portfolio membership.
- `profiles.yml`: portable Workspace Profile definitions.
- `active-profile.txt`: machine-local profile selection.
- `portfolio-projects/<id>/.controltower/project.yml`: durable project identity.
- Git: disposable repository state.
- GitHub or Azure DevOps: their own hosted work-item truth.
- Windows Credential Manager: secrets and trusted SSH fingerprints.
- `library/library.yml`: user-owned reusable asset index.

## Operating model

- Startup is cache-first and does not require a network.
- Scans are explicit and read-only unless the user starts a lifecycle action.
- Local, SSH, hosted-only, and hybrid workspaces are first-class.
- Derived state is disposable and recomputed rather than synchronized.
- Mutable data never lives inside Velopack's replaceable `current` directory.
- Package updates require explicit confirmation and use separate x64/ARM64
  channels.

## Security boundaries

External paths and URLs are normalized and validated before use. SSH host keys
use trust-on-first-use confirmation and are stored in Credential Manager.
Credentials are never written to YAML or logs. Release automation uses
least-privilege permissions, immutable action references, dependency review,
CodeQL, workflow analysis, SBOM generation, checksums, and provenance
attestations.
