# .NET 10 upgrade

Status: approved, not started. Driver is security support, not features.

## Why

.NET 8 reaches end of support on **10 November 2026**. Confirmed from the
official release metadata channel for 8.0:

| Field | Value |
|---|---|
| `eol-date` | `2026-11-10` |
| `support-phase` | `maintenance` |
| `latest-sdk` / `latest-runtime` | `8.0.424` / `8.0.30` |

The repository is already on the last .NET 8 SDK and runtime, so there is no
remaining patch headroom. After that date the self-contained packages we ship
would embed a runtime that receives no security fixes.

Target is **.NET 10 (LTS)**: `latest-sdk 10.0.400`, `latest-runtime 10.0.11`,
`eol-date 2028-11-14`, `support-phase active`.

The benefit is support, and it is worth stating what it is *not*. There is no
meaningful performance win for a mostly-idle WPF console, no WPF feature we
consume, and no language feature we need. What we do get is a runtime that keeps
receiving CVE fixes — which matters here because the project already ships SPDX
SBOMs, build provenance attestations and Scorecard results. An unsupported
runtime inside our own attested SBOM undermines that apparatus.

## Roadmap

| Phase | Deliverable | Ships as | Target date |
|---|---|---|---|
| 0 | Bridge release — updater becomes framework-agnostic | .NET 8 release | **5 Sep 2026** |
| 1 | Adoption gate — every install running the bridge build | (no release) | **19 Sep 2026** |
| 2 | The retarget to .NET 10 | .NET 10 release | **10 Oct 2026** |
| 3 | Dependency hygiene | patch release | after Phase 2, no deadline |

**Status.** Phase 0 shipped as `v0.11.0-preview.1` and Phase 1 is **satisfied**:
the install reports `0.11.0-preview.1+5f5bc34`, so it carries the
framework-agnostic updater and can cross the TFM boundary. Phase 2 is therefore
unblocked and ships as `0.12.0-preview.1`. Phase 3 remains deferred.

Re-check the gate before tagging if any further install exists — adoption means
each install *reporting* the bridge version, not the release merely being
published.

Dates leave roughly four weeks of slack before the 10 November deadline. That
slack is deliberate: if Phase 2 validation surfaces a problem in composite
ReadyToRun on `win-arm64` or in the test runner, the fix window should be weeks,
not days.

### Release mechanics

Both release phases go out through `.github/workflows/release.yml`, which fires
on a `v*` tag and **fails the build unless the tag matches `<Version>` in
`ControlTower.Desktop.csproj`**. Bump the csproj version in the same commit as
the tag. A version containing `-` is published as a prerelease, which is the
current convention (`0.11.0-preview.1` carries the Phase 0 bridge).

### Critical path

**Phase 1 is a hard gate, not a formality.** Phase 2 must not land until every
install is running the Phase 0 build. An install still on the current binary has
`-f net8.0-windows` baked into its update script and cannot cross the TFM
boundary — see Phase 0 below. The consequence of getting this order wrong is a
broken install requiring manual recovery, not a failed build.

This is also why Phase 0 should ship soon even though the deadline is months
away. Leaving both phases to November forces them into a rushed sequence with no
adoption window between them.

### Phase 0 has standalone value

Even if the .NET 10 move were abandoned, Phase 0 is worth shipping. It fixes a
latent bug that will bite on *any* future framework change — including a routine
.NET 8 patch bump that shifts the pinned SDK.

## Phase 0 — bridge release (stays on .NET 8)

**Why it exists:** a single retarget commit **will break in-place self-update for
every existing source install.**

`src/ControlTower.Infrastructure/Update/UpdateService.cs:41` bakes the target
framework into a constant:

```csharp
private const string DesktopTargetFramework = "net8.0-windows";
```

The generated update script uses it *after* fast-forwarding the checkout: the
script runs `git merge --ff-only FETCH_HEAD`, then
`dotnet publish "%CSPROJ%" -c Release -f net8.0-windows --no-self-contained`.

The script is written by the **currently installed** binary. So an installed
.NET 8 build will advance the checkout onto the .NET 10 commit and then publish
with `-f net8.0-windows` against a project that no longer offers that target.
The publish fails and the app reports its own worst case verbatim:

> `*** Publish failed. Source was updated but the OLD build is still installed. ***`

Recovery requires a manual re-install. The same constant is duplicated in
`Install-DeveloperControlTower.ps1:342`.

### Discovered during implementation: the source-install path was already broken

Verifying the fix surfaced a **pre-existing** bug that made Phase 0 larger than
scoped. `ControlTower.Desktop.csproj` set:

```xml
<PublishReadyToRun>true</PublishReadyToRun>
<PublishReadyToRunComposite>true</PublishReadyToRunComposite>
```

unconditionally, alongside `UseCurrentRuntimeIdentifier=false`. ReadyToRun
cannot infer a RID under those settings, so **any publish without an explicit
`-r` failed**:

```
error NETSDK1191: A runtime identifier for the property 'PublishReadyToRun'
couldn't be inferred. Specify a rid explicitly.
```

That is exactly the command both `Install-DeveloperControlTower.ps1` and the
in-place updater run. Measured on all four flag combinations — with and without
`-f`, with and without `--no-restore` — every one failed, including the
currently shipped command. The fault dates to the initial public release
(`7106f1f`).

It stayed invisible because the release job always publishes with `-r win-x64`
or `-r win-arm64`, and CI only built and tested — nothing ever exercised a
RID-less publish.

Both properties are now gated on `'$(RuntimeIdentifier)' != ''`. Verified by
property evaluation:

| Publish | `PublishReadyToRun` | `PublishReadyToRunComposite` |
|---|---|---|
| RID-less (install / update) | *(empty)* | `false` |
| `-r win-x64 --self-contained` | `true` | `true` |

So packaged releases keep composite pre-JIT, and the source-install path builds.
A `Verify source-install publish path` step in `ci.yml` now guards it, and also
fails if restore or publish modifies a tracked file.

### Work

Make the updater framework-agnostic *before* the framework moves:

1. Drop `-f <tfm>` from the generated publish command and from
   `Install-DeveloperControlTower.ps1`. Both projects are single-target, so the
   SDK selects the right TFM by itself. Retire the constant.
2. Add an SDK preflight. `global.json` uses `rollForward: disable`, so the build
   needs *exactly* the pinned SDK — but
   `Install-DeveloperControlTower.ps1:319-320` only checks that `dotnet` exists
   on PATH, and the updater has no check at all. Parse `sdk.version` from
   `global.json` and match it against `dotnet --list-sdks`.
3. In the updater, read the incoming pin with `git show FETCH_HEAD:global.json`
   and abort **before** merging if the required SDK is absent. This is what
   keeps the checkout from being left ahead of the installed binary.
4. Prefer locked restore followed by `publish --no-restore` in the generated
   script, so an install cannot silently drift off the committed lock files.
5. Cover the generated publish command with an assertion in
   `UpdateServiceTests` — specifically that it carries no `-f`, and that the
   preflight aborts before any merge.
6. Gate ReadyToRun on an explicit RID, and guard the RID-less publish in CI
   (see the discovery above).

### Exit criteria

- Generated update script contains no target-framework flag.
- Missing-SDK case aborts before the merge, leaving the checkout unmoved.
- A RID-less `dotnet publish` of the desktop project succeeds.
- Packaged release builds still resolve `PublishReadyToRun=true`.
- Released and tagged.

Users who skip this release still need a manual re-install when Phase 2 lands.
That is unavoidable and should be called out in the release notes.

## Phase 1 — adoption gate

**Satisfied on 28 Aug 2026.** No code.

Confirm every install is running the Phase 0 build before Phase 2 is merged.
Check the version each install reports; do not infer adoption from the fact that
a release was published.

Evidence for the current install:

| Check | Value |
|---|---|
| `current\ControlTower.Desktop.exe` `ProductVersion` | `0.11.0-preview.1+5f5bc34` |
| Commit embedded in that version | `5f5bc34`, the Phase 0 bridge merge |
| `runtimeconfig.json` | self-contained, `Microsoft.NETCore.App` `8.0.30` |

The embedded commit is what matters: it proves the binary carries the
framework-agnostic updater rather than merely sharing a version string.

If an install cannot be upgraded in time, it needs a manual re-install after
Phase 2 rather than an in-place update.

## Phase 2 — the retarget

### Change inventory

Verified by reading each file; line numbers are current as of this document.

#### SDK pin

| File | Change |
|---|---|
| `global.json:3` | `8.0.424` → `10.0.400`, keep `rollForward: disable` |
| `Build-ReleasePackages.ps1:124-127` | hardcoded `'8.0.424'` in `Assert-ReleaseMetadata` — update **both** the condition and the throw message |

CI needs no edit: `ci.yml`, `release.yml` and `codeql.yml` all pass
`global-json-file: global.json` to `actions/setup-dotnet` and follow the pin.

#### Target frameworks

| File | Change |
|---|---|
| `src/ControlTower.Core/ControlTower.Core.csproj:3` | `net8.0` → `net10.0` |
| `src/ControlTower.Infrastructure/ControlTower.Infrastructure.csproj:3` | `net8.0` → `net10.0` |
| `src/ControlTower.Tests/ControlTower.Tests.csproj:4` | `net8.0` → `net10.0` |
| `src/ControlTower.Desktop/ControlTower.Desktop.csproj:3` | `net8.0-windows` → `net10.0-windows` |

#### Lock files

All ten regenerate. Keys move `net8.0` → `net10.0` and
`net8.0-windows7.0` → `net10.0-windows7.0`, plus the `/win-x64` and
`/win-arm64` variants.

#### Runtime licences

Rename `licenses/dotnet-runtime-8.0.30/` → `licenses/dotnet-runtime-10.0.11/`
and re-download both files from the `v10.0.11` tag of `dotnet/runtime`
(confirmed reachable: `LICENSE.TXT` 1,116 bytes;
`THIRD-PARTY-NOTICES.TXT` 76,623 bytes).

Referenced from four places:

- `Build-ReleasePackages.ps1:22` — `$runtimeVersion = '8.0.30'`
- `Test-PublicSourceBoundary.ps1:198-199` — allowlist
- `src/ControlTower.Desktop/ControlTower.Desktop.csproj:58-63` — two `Content`
  includes with matching `Link` attributes
- `THIRD_PARTY_NOTICES.md:9,17,20-26,37`

#### Transitive packages still on the .NET 8 train

Retargeting alone does **not** fix this. `SSH.NET 2026.0.0` pins
`Microsoft.Extensions.Logging.Abstractions 8.0.3` in *every* dependency group
**including its `net10.0` group**, and that package drags in
`Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2`. Both reach end of
support on the same date as .NET 8, and both are redistributed in our packages.

`CentralPackageTransitivePinningEnabled` is already `true`, so the fix is two
central pins in `Directory.Packages.props` at `10.0.11` (both confirmed present
on the proxy feed):

```xml
<PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.11" />
<PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.11" />
```

Then update `THIRD_PARTY_NOTICES.md:10-11` and the provenance link at line 37
(`v8.0.30` → `v10.0.11`).

#### Documentation and comments

| File | Change |
|---|---|
| `docs/architecture.md:3` | ".NET 8 WPF application" |
| `Install-DeveloperControlTower.ps1:320` | error text — reword to name the pinned SDK, not ".NET 8 SDK or later" |
| `src/ControlTower.Tests/UpdateServiceTests.cs:444` | `net8.0-windows` in a fixture path |
| `src/ControlTower.Infrastructure/Theme/MicaSupportPolicy.cs:11` | stale comment |
| `src/ControlTower.Infrastructure/Theme/SystemThemeReader.cs:12` | stale comment |

`CONTRIBUTING.md` needs no change — it defers to `global.json` by name.

### Discovered during implementation: ReadyToRun packs are a restore-time input

The retarget passed restore, build, all 785 tests, the boundary check and the
NuGet audit, then failed the release build:

```
error NETSDK1094: Unable to optimize assemblies for performance: a valid
runtime package was not found. ... make sure to restore packages with the
PublishReadyToRun property set to true.
```

`Build-ReleasePackages.ps1` restores each RID with `--locked-mode` and then
publishes `--self-contained true --no-restore`. The crossgen2 pack that
ReadyToRun needs is acquired during **restore**, and the restore was not being
told that a ReadyToRun self-contained publish was coming.

Measured with a deliberately cold cache (the 10.0.11 crossgen2 pack deleted
from `~/.nuget/packages` first):

| RID restore | crossgen2 10.0.11 fetched | `publish --no-restore` |
|---|---|---|
| as before | no | fails `NETSDK1094` |
| `-p:PublishReadyToRun=true -p:SelfContained=true` | yes | succeeds |

This was masked on .NET 8 purely because the 8.0.30 crossgen2 pack was already
sitting in the local package cache from earlier builds — a cold machine would
have hit the same wall. The RID restore now passes both properties, keeping it
in step with the publish that follows. This also resolves the restore/publish
`SelfContained` mismatch previously listed under risks.

### Sequencing

Order matters; two steps will fail if run early.

1. Install SDK `10.0.400` locally. The machine currently has the .NET 10
   **runtimes** (`10.0.11`) but **no** .NET 10 SDK.
2. Retarget the four projects and add the two central pins.
3. Regenerate the four base lock files.
4. Regenerate the six RID lock files with
   `-p:PublishReadyToRun=true -p:SelfContained=true`, matching the release
   script's RID restore.
5. Assert the new lock keys really say `net10.0` / `net10.0-windows7.0`.
   `Assert-ReleaseMetadata` only checks that a RID target *ends with* the
   expected RID; it would happily pass a stale `net8.0` graph.
6. **Stage** the licence rename and the `Test-PublicSourceBoundary.ps1` edit
   before the full release run — that script reads the Git *index*, not the
   working tree, so unstaged renames fail the boundary check.
7. `dotnet restore --locked-mode` → `build` → `test`.
8. Full `.\Build-ReleasePackages.ps1` for both RIDs.

### Validation gates

- `dotnet restore DeveloperControlTower.sln --locked-mode` clean.
- `dotnet test` green. This is the empirical gate for the deferred test stack.
- `Build-ReleasePackages.ps1` completes for `win-x64` and `win-arm64`.
- **Launch the published self-contained output for both RIDs.** The release
  script only checks the PE machine type; it never proves the app starts.
- Visual smoke test including hyphenated text (paths, URLs, branch names).
  WPF gained hyphen-based ligature rendering in .NET 9, which we jump over.
  `Themes/Typography.xaml:7-16` defines the font chains and nothing in the app
  sets `Typography.StandardLigatures` (the only `Typography.*` setter is
  `NumeralAlignment` at `App.xaml:460`).

## Phase 3 — dependency hygiene

Deliberately held back so the framework move stays a single variable. Pick these
up once .NET 10 is shipped and stable:

| Package | Current | Latest on proxy feed |
|---|---|---|
| `Microsoft.NET.Test.Sdk` | 17.14.1 | 18.9.0 |
| `xunit.runner.visualstudio` | 2.8.2 | 4.0.0 |
| `coverlet.collector` | 6.0.4 | 10.0.1 |
| `YamlDotNet` | 16.3.0 | 18.1.0 |

Deferral is safe. `Microsoft.NET.Test.Sdk 17.14.1` ships `net8.0` build and
testhost assets, which NuGet considers compatible with `net10.0`;
`xunit.runner.visualstudio 2.8.2` ships a `net6.0` adapter, likewise
compatible. .NET 10 keeps VSTest as the `dotnet test` default and this repo does
not opt into Microsoft.Testing.Platform. **Do not** opt in during this work —
xUnit v2 would then need its own migration.

Residual .NET 8-versioned packages were expected to survive in the test graph
(`System.Reflection.Metadata 8.0.0`, `System.Collections.Immutable 8.0.0`, both
from the test SDK). They did not: after the retarget no package in any of the
ten lock files resolves to an 8.x version. Verified by searching every
`packages*.lock.json` for `"resolved": "8.` — no matches.

## Risks

**No code-level breaking changes found.** Grepped for `BinaryFormatter`,
obsolete `X509Certificate2` constructors, `RNGCryptoServiceProvider`,
`WebRequest` and `ProtectedData` — none present. The only
runtime-location APIs in use are `AppContext.BaseDirectory` and
`Process.GetCurrentProcess().MainModule`, both stable.

**Expect a `runtimeconfig.json` diff.** On `net8.0` the WPF SDK emitted
`System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization: true`.
`BinaryFormatter` was removed from the runtime in .NET 9, so on `net10.0` that
switch is emitted as `false` and `CSWINRT_USE_WINDOWS_UI_XAML_PROJECTIONS`
appears alongside it. Nothing in this repository uses `BinaryFormatter` — treat
the change as expected, not a regression.

**WPF theming is unaffected.** `App.xaml:8-13` merges only repository-owned
dictionaries (`Colors`, `Spacing`, `Typography`, `Components`), there is no
`ThemeMode` or `PresentationFramework.Fluent` reference, and the app applies its
own brushes and Mica from `App.xaml.cs`. WPF's default remains `ThemeMode=None`,
so the .NET 9/10 Fluent work does not replace this app's appearance.

**Velopack is already .NET 10 ready.** `vpk 1.2.0` ships a `tools/net10.0`
asset, so `"rollForward": false` in `.config/dotnet-tools.json` is safe under an
SDK 10 pin. `Velopack 1.2.0` has a `net10.0` lib target. No version change.

**Composite ReadyToRun stays supported**, including x64-hosted
cross-compilation to `win-arm64`. Nothing was removed in .NET 10.

**RID restore/publish property mismatch — now fixed.**
`Build-ReleasePackages.ps1` used to restore with a RID but leave `SelfContained`
false, then publish with `--self-contained true --no-restore`, so restore and
publish evaluated different inputs. On .NET 10 this stopped being theoretical
and produced `NETSDK1094`. The RID restore now passes
`-p:PublishReadyToRun=true -p:SelfContained=true`; see the Phase 2 discovery
above.

**Local NuGet configuration.** `nuget.org` is disabled and all restore flows
through `https://packagefeedproxy.microsoft.io/nuget/v3/index.json`. That proxy
is registered **twice** under different source names (`azure-default` and
`msft-proxy-nuget`), which can raise `NU1507` under Central Package Management.
This is user-level configuration, not a repository invariant — the repo has no
checked-in `NuGet.Config`. Every package needed for this upgrade was confirmed
present on the proxy.
