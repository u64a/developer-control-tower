# Developer Control Tower contributor instructions

Developer Control Tower is a lightweight, personal Windows execution console
for portfolio awareness, repository truth, durable context, reusable assets,
and fast launch into work.

- Keep the app single-user, local-first, and useful while offline.
- Do not add sprint tracking, estimates, capacity, collaboration, dashboards,
  background services, or hidden synchronization.
- Keep business logic in Core, I/O in Infrastructure, and composition/UI in
  Desktop.
- Never hardcode user paths, tenants, hosts, tokens, or machine-specific values.
- Keep secrets in Windows Credential Manager and redact logs.
- Validate every external path, URL, YAML value, Git remote, and SSH input.
- Preserve stable local project IDs when attaching hosted-system IDs.
- Prefer explicit user action and read-only behavior.
- Keep mutable data outside the Velopack install directory.
- GitHub Actions are permitted only under `.github/workflows`, must use
  least-privilege permissions and full immutable action SHAs, and must never
  use `pull_request_target`.
- Do not commit portfolio state, private assets, logs, credentials, agent
  history, or generated release output.
