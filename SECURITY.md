# Security policy

## Supported versions

Security fixes are applied to the latest published release. Preview releases
may change quickly; users should update to the newest preview before reporting
an issue that is already fixed.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Use
[GitHub private vulnerability reporting](https://github.com/u64a/developer-control-tower/security/advisories/new)
so maintainers can investigate without exposing users.

Include:

- the affected version and Windows architecture;
- the expected and observed behavior;
- a minimal reproduction;
- the security impact;
- any logs after removing personal paths and confidential project data.

Never include passwords, tokens, private keys, repository contents, or
customer data. The maintainers will acknowledge a complete report, assess its
impact, and coordinate disclosure and a fix where appropriate.

## Security design

- Secrets stay in Windows Credential Manager.
- External paths, URLs, YAML, Git remotes, and SSH host keys are validated.
- Repository scans are read-only by default.
- Updates require explicit user confirmation.
- Release workflows use least-privilege tokens, immutable action SHAs,
  checksums, SBOMs, and GitHub artifact attestations.

Preview installers are currently unsigned. Windows SmartScreen or Defender may
therefore show a reputation warning. Verify downloads against the published
SHA-256 files and GitHub attestations.
