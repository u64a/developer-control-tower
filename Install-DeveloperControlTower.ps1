<#
.SYNOPSIS
    Installs (or upgrades) Developer Control Tower into a stable Program Files
    location and writes the sentinel file the in-app updater needs.

.DESCRIPTION
    The app's canonical install location is OUTSIDE the source clone (so the
    repo stays free of bin/obj artifacts and a source update never collides with
    the running .exe). This script:

      1. Publishes a Release build of the Desktop project into a temp
         staging folder.
      2. Self-elevates with UAC if the chosen install directory is not
         writable by the current user (the common case for the default
         "%ProgramFiles%\Development Tower" target).
      3. Robocopies the staging folder over the install directory (/MIR
         so stale assemblies from a prior version are removed, but with
         /XF on the sentinel and ownership marker so installer metadata
         survives).
      4. Writes update-repo-root.txt next to the installed .exe so the
         in-app self-updater can locate this source clone for the next
         verified fast-forward + `dotnet publish` cycle, and writes an ownership marker
         identifying the destination as an app-managed install.
      5. Runs `dotnet clean` on the source clone to keep the repo small.
      6. Launches the installed app (in the user's session, not elevated).

.PARAMETER InstallDir
    Target install directory. Defaults to "%ProgramFiles%\Development Tower".
    This must be a dedicated Developer Control Tower directory. A nonempty
    directory is accepted only when it was created by this installer or is a
    recognizable legacy install.

.PARAMETER NoLaunch
    Skip the final "launch the installed app" step.

.PARAMETER SkipClean
    Skip the `dotnet clean` of the source clone after install.

.EXAMPLE
    .\Install-DeveloperControlTower.ps1
    Installs into the default Program Files location, prompting for UAC.

.EXAMPLE
    .\Install-DeveloperControlTower.ps1 -InstallDir 'C:\Tools\DevTower'
    Installs into a user-writable location with no UAC prompt.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'Development Tower'),
    [switch]$NoLaunch,
    [switch]$SkipClean,

    # Internal non-destructive validation seam. Performs all destination
    # checks without publishing, elevating, or mirroring.
    [switch]$ValidateInstallDirOnly,

    # Internal parameters used when the script self-elevates. Not for
    # interactive use - they let the elevated child skip the publish
    # step (which the parent already did under the user's context) and
    # know which source clone to record in the sentinel.
    [string]$StagingPath = '',
    [string]$RepoRoot = ''
)

$ErrorActionPreference = 'Stop'

$ExeName                = 'ControlTower.Desktop.exe'
$SentinelName           = 'update-repo-root.txt'
$OwnershipMarkerName    = '.developer-control-tower-install'
$OwnershipMarkerContent = 'Developer Control Tower managed install v1'

function Test-IsAdmin {
    $id  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr  = New-Object Security.Principal.WindowsPrincipal($id)
    return $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-IsWritable([string]$dir) {
    try {
        if (-not (Test-Path -LiteralPath $dir)) {
            $parent = Split-Path -Parent $dir
            if ([string]::IsNullOrWhiteSpace($parent) -or -not (Test-Path -LiteralPath $parent)) {
                return $false
            }
            $dir = $parent
        }
        $probe = Join-Path $dir ('ctlow-probe-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.tmp')
        [System.IO.File]::WriteAllText($probe, 'x')
        Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue
        return $true
    }
    catch {
        return $false
    }
}

function ConvertTo-CanonicalPath([string]$path, [string]$description) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "$description must not be empty."
    }

    try {
        $fullPath = [System.IO.Path]::GetFullPath($path)
    }
    catch {
        throw "$description is not a valid Windows path: $path"
    }

    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if (-not [string]::Equals($fullPath, $root, [StringComparison]::OrdinalIgnoreCase)) {
        $fullPath = $fullPath.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    }
    return $fullPath
}

function Test-SamePath([string]$left, [string]$right) {
    return [string]::Equals($left, $right, [StringComparison]::OrdinalIgnoreCase)
}

function Test-PathWithinOrSame([string]$candidate, [string]$root) {
    if (Test-SamePath $candidate $root) {
        return $true
    }

    $rootPrefix = $root
    if (-not $rootPrefix.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString())) {
        $rootPrefix += [System.IO.Path]::DirectorySeparatorChar
    }
    return $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Test-PathsOverlap([string]$left, [string]$right) {
    return (Test-PathWithinOrSame $left $right) -or
        (Test-PathWithinOrSame $right $left)
}

function Get-DangerousInstallRoots {
    $candidates = @(
        $env:SystemRoot,
        $env:windir,
        [Environment]::SystemDirectory,
        $env:USERPROFILE,
        $env:PUBLIC,
        $env:ProgramData,
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:ProgramW6432,
        $env:TEMP,
        $env:TMP,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86),
        [System.IO.Path]::GetTempPath()
    )

    $roots = @()
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $roots += ConvertTo-CanonicalPath $candidate 'Protected Windows directory'
        }
    }
    return $roots | Select-Object -Unique
}

function Get-SentinelRepoRoot([string]$sentinelPath) {
    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw "Legacy install sentinel is missing or is not a file: $sentinelPath"
    }

    foreach ($rawLine in Get-Content -LiteralPath $sentinelPath) {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
            continue
        }
        if ($line.Length -ge 2 -and $line[0] -eq '"' -and $line[$line.Length - 1] -eq '"') {
            $line = $line.Substring(1, $line.Length - 2).Trim()
        }
        return ConvertTo-CanonicalPath $line 'Legacy install sentinel repo root'
    }

    throw "Legacy install sentinel does not contain a repo root: $sentinelPath"
}

function Assert-NoReparsePointAncestors([string]$path) {
    $current = $path
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "InstallDir must not be inside a reparse point: $current"
            }
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or (Test-SamePath $parent $current)) {
            break
        }
        $current = $parent
    }
}

function Assert-SafeInstallDestination(
    [string]$installPath,
    [string]$sourcePath,
    [string]$stagePath = '') {

    $driveOrShareRoot = [System.IO.Path]::GetPathRoot($installPath)
    if (Test-SamePath $installPath $driveOrShareRoot) {
        throw "InstallDir cannot be a drive or share root: $installPath"
    }

    $parent = Split-Path -Parent $installPath
    if (Test-SamePath $parent $driveOrShareRoot) {
        throw "InstallDir is too broad. Choose a dedicated application directory below: $installPath"
    }

    foreach ($dangerousRoot in Get-DangerousInstallRoots) {
        if (Test-SamePath $installPath $dangerousRoot) {
            throw "InstallDir is a protected or broadly shared Windows directory: $installPath"
        }
    }

    if (Test-PathsOverlap $installPath $sourcePath) {
        throw "InstallDir and the source repository must not overlap: '$installPath' and '$sourcePath'."
    }
    if (-not [string]::IsNullOrWhiteSpace($stagePath) -and
        (Test-PathsOverlap $installPath $stagePath)) {
        throw "InstallDir and the publish staging directory must not overlap: '$installPath' and '$stagePath'."
    }

    Assert-NoReparsePointAncestors $installPath

    if (-not (Test-Path -LiteralPath $installPath)) {
        return
    }
    if (-not (Test-Path -LiteralPath $installPath -PathType Container)) {
        throw "InstallDir exists but is not a directory: $installPath"
    }

    $installItem = Get-Item -LiteralPath $installPath -Force
    if (($installItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "InstallDir must not be a reparse point: $installPath"
    }

    $entries = @(Get-ChildItem -LiteralPath $installPath -Force)
    if ($entries.Count -eq 0) {
        return
    }

    $markerPath = Join-Path $installPath $OwnershipMarkerName
    if (Test-Path -LiteralPath $markerPath) {
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "InstallDir has a malformed ownership marker: $markerPath"
        }
        $markerValue = (Get-Content -LiteralPath $markerPath -Raw).TrimEnd("`r", "`n")
        if (-not [string]::Equals(
            $markerValue,
            $OwnershipMarkerContent,
            [StringComparison]::Ordinal)) {
            throw "InstallDir has a malformed ownership marker: $markerPath"
        }
        return
    }

    $legacyExe = Join-Path $installPath $ExeName
    $legacySentinel = Join-Path $installPath $SentinelName
    if ((Test-Path -LiteralPath $legacyExe -PathType Leaf) -and
        (Test-Path -LiteralPath $legacySentinel -PathType Leaf)) {
        $legacyRepoRoot = Get-SentinelRepoRoot $legacySentinel
        if (-not (Test-SamePath $legacyRepoRoot $sourcePath)) {
            throw "Legacy install sentinel does not point to this source repository: $legacySentinel"
        }
        return
    }

    throw "Refusing to mirror into nonempty unowned directory: $installPath"
}

# --- Resolve and validate paths before publishing or requesting elevation. ---
try {
    if (-not $RepoRoot) {
        $RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $RepoRoot = ConvertTo-CanonicalPath $RepoRoot 'RepoRoot'
    $InstallDir = ConvertTo-CanonicalPath $InstallDir 'InstallDir'
    if ($StagingPath) {
        $StagingPath = ConvertTo-CanonicalPath $StagingPath 'StagingPath'
    }
    Assert-SafeInstallDestination $InstallDir $RepoRoot $StagingPath
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}

$slnPath = Join-Path $RepoRoot 'DeveloperControlTower.sln'
if (-not (Test-Path -LiteralPath $slnPath -PathType Leaf)) {
    Write-Error "Install script must live next to DeveloperControlTower.sln. Not found at: $slnPath"
    exit 1
}

$ProjectPath = Join-Path $RepoRoot 'src\ControlTower.Desktop\ControlTower.Desktop.csproj'
$isGitCheckout = Test-Path -LiteralPath (Join-Path $RepoRoot '.git')

if ($ValidateInstallDirOnly) {
    Write-Host "Install directory validation passed: $InstallDir" -ForegroundColor Green
    exit 0
}

# --- Step 1: dotnet publish into staging (skipped if we're the elevated child). ---
if (-not $StagingPath) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Error "The .NET SDK ('dotnet') was not found on PATH. Install .NET 8 SDK or later."
        exit 1
    }

    if (-not $isGitCheckout) {
        Write-Warning ("'$RepoRoot' is not a Git checkout. Installation will work, " +
            "but Settings -> Check for updates will be unavailable. Use 'git clone' " +
            "instead of Download ZIP to enable self-update.")
    }

    $StagingPath = Join-Path $env:TEMP ('controltower-install-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
    try {
        $StagingPath = ConvertTo-CanonicalPath $StagingPath 'StagingPath'
        Assert-SafeInstallDestination $InstallDir $RepoRoot $StagingPath
    }
    catch {
        Write-Error $_.Exception.Message
        exit 1
    }
    Write-Host "[1/4] Publishing Release build to staging: $StagingPath" -ForegroundColor Cyan
    & dotnet publish $ProjectPath `
        -c Release `
        -f net8.0-windows `
        --no-self-contained `
        --nologo `
        -o $StagingPath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed (exit $LASTEXITCODE)."
        exit $LASTEXITCODE
    }
}

# --- Step 2: elevate if needed, then robocopy + sentinel + clean. ---
$isElevatedChild = $PSBoundParameters.ContainsKey('StagingPath')

try {
    Assert-SafeInstallDestination $InstallDir $RepoRoot $StagingPath
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}

$needsElevation  = -not (Test-IsWritable $InstallDir)

if ($needsElevation -and -not (Test-IsAdmin)) {
    Write-Host "[2/4] Install directory '$InstallDir' requires elevation. Re-launching with UAC..." -ForegroundColor Yellow

    # The elevated child does NOT launch the app - we want it to run in
    # the user's session, not elevated. The parent handles -NoLaunch.
    #
    # Start-Process -ArgumentList joins array entries with spaces and
    # does NOT auto-quote values containing spaces. Paths like
    # "C:\Program Files\Development Tower" must be wrapped in double
    # quotes explicitly or PowerShell in the elevated child will parse
    # the second word as an unknown positional argument.
    function Quote-Arg([string]$v) {
        return '"' + $v.Replace('"', '\"') + '"'
    }
    $childArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', (Quote-Arg $PSCommandPath),
        '-InstallDir', (Quote-Arg $InstallDir),
        '-StagingPath', (Quote-Arg $StagingPath),
        '-RepoRoot', (Quote-Arg $RepoRoot),
        '-NoLaunch'
    )
    if ($SkipClean) { $childArgs += '-SkipClean' }

    $proc = Start-Process -FilePath 'powershell' -Verb RunAs -ArgumentList $childArgs -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        Write-Error "Elevated install step failed (exit $($proc.ExitCode))."
        exit $proc.ExitCode
    }
}
else {
    if (-not (Test-Path -LiteralPath $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir | Out-Null
    }

    try {
        Assert-SafeInstallDestination $InstallDir $RepoRoot $StagingPath
    }
    catch {
        Write-Error $_.Exception.Message
        exit 1
    }
    if (-not (Test-Path -LiteralPath (Join-Path $StagingPath $ExeName) -PathType Leaf)) {
        Write-Error "Staging output is missing the expected executable: $ExeName"
        exit 1
    }

    Write-Host "[2/4] Mirroring publish output into: $InstallDir" -ForegroundColor Cyan
    # /MIR mirrors (delete extras) so a prior build's stale DLLs are
    # removed. /XF preserves installer metadata between versions. /XJ
    # avoids following directory junctions. /NFL /NDL /NP keep the log compact.
    $rcArgs = @(
        $StagingPath, $InstallDir,
        '/MIR', '/XJ', '/XD', 'library', '/XF', $SentinelName, $OwnershipMarkerName,
        '/R:2', '/W:2',
        '/NFL', '/NDL', '/NP'
    )
    & robocopy @rcArgs | Out-Null
    $rc = $LASTEXITCODE
    if ($rc -ge 8) {
        Write-Error "robocopy failed (exit $rc). Install at '$InstallDir' may be partial."
        exit $rc
    }

    Write-Host "[3/4] Writing sentinel: $SentinelName -> $RepoRoot" -ForegroundColor Cyan
    $sentinelPath = Join-Path $InstallDir $SentinelName
    $sentinelLines = @(
        '# Developer Control Tower update sentinel',
        '# Written by Install-DeveloperControlTower.ps1',
        '# Edit only if you intentionally move the source folder.',
        $RepoRoot
    )
    Set-Content -LiteralPath $sentinelPath -Value $sentinelLines -Encoding utf8

    $ownershipMarkerPath = Join-Path $InstallDir $OwnershipMarkerName
    Set-Content -LiteralPath $ownershipMarkerPath -Value $OwnershipMarkerContent -Encoding ascii

    if (-not $SkipClean) {
        Write-Host "[4/4] Cleaning bin/obj from the source clone..." -ForegroundColor Cyan
        & dotnet clean $slnPath -c Release --nologo | Out-Null
    }
}

# --- Step 3 (parent only): launch the installed app in the user's session. ---
if (-not $isElevatedChild) {
    # Tidy staging now that the install (whether direct or via the
    # elevated child) is done.
    Remove-Item -LiteralPath $StagingPath -Recurse -Force -ErrorAction SilentlyContinue

    $exePath = Join-Path $InstallDir $ExeName
    Write-Host ""
    Write-Host "Installed: $exePath" -ForegroundColor Green
    Write-Host "Source folder: $RepoRoot" -ForegroundColor Green

    if (-not $NoLaunch) {
        Write-Host "Launching $ExeName..." -ForegroundColor Cyan
        Start-Process -FilePath $exePath
    }
}

exit 0
